using BLL.Models;
using BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

/// <summary>
/// Controller for SNOMED CT terminology seeding operations.
/// Automatically uses the configured import directory (snomed-data/import).
/// Supports pause/resume and crash recovery via persistent checkpoints.
/// 
/// WARNING: Seeding operations are resource-intensive and should be protected
/// in production environments. Consider adding authorization.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SnomedController : ControllerBase
{
    private readonly SnomedSeederService _seeder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SnomedController> _logger;

    public SnomedController(
        SnomedSeederService seeder,
        IServiceScopeFactory scopeFactory,
        ILogger<SnomedController> logger)
    {
        _seeder = seeder;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current status of SNOMED CT terminology and any active seeding job.
    /// </summary>
    /// <returns>Status information including concept count, seeding state, and configured paths.</returns>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SnomedFullStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var isSeeded = await _seeder.IsSeededAsync(ct);
        var conceptCount = await _seeder.GetConceptCountAsync(ct);
        var seedStatus = _seeder.GetStatus();

        return Ok(new SnomedFullStatusResponse(
            IsSeeded: isSeeded,
            ConceptCount: conceptCount,
            ImportDirectory: _seeder.ImportDirectory,
            SnapshotDirectory: _seeder.SnapshotDirectory,
            Message: isSeeded
                ? $"SNOMED CT terminology loaded with {conceptCount:N0} concepts"
                : "SNOMED CT terminology not yet seeded. Place RF2 files in " + _seeder.ImportDirectory,
            SeedingJob: seedStatus));
    }

    /// <summary>
    /// Initiates or resumes SNOMED CT terminology seeding from the import directory.
    /// Returns immediately with 202 Accepted - seeding runs in background.
    /// 
    /// Expects unpacked RF2 files at: snomed-data/import/Snapshot/Terminology/
    /// 
    /// This is a long-running operation (~1 hour for full International Edition).
    /// Poll GET /api/snomed/status or /api/snomed/job to monitor progress.
    /// Supports pause/resume - if a previous job was paused or crashed, it will resume.
    /// 
    /// POST /api/snomed/seed
    /// POST /api/snomed/seed?forceRestart=true  (ignores checkpoint, starts fresh)
    /// </summary>
    /// <param name="activeOnly">Whether to load only active concepts (default: true).</param>
    /// <param name="batchSize">Batch size for bulk operations (default: 1000).</param>
    /// <param name="forceRestart">If true, ignores existing checkpoint and starts fresh.</param>
    /// <returns>Accepted response with job info. Poll /status for progress.</returns>
    [HttpPost("seed")]
    [ProducesResponseType(typeof(SnomedSeedStartedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult Seed(
        [FromQuery] bool activeOnly = true,
        [FromQuery] int batchSize = 1000,
        [FromQuery] bool forceRestart = false)
    {
        // Check for existing job status
        var existingStatus = _seeder.GetStatus();
        if (existingStatus is not null && existingStatus.IsRunning)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Seeding In Progress",
                Detail = $"A seeding job is already running (JobId: {existingStatus.JobId}, Phase: {existingStatus.Phase}). Poll /api/snomed/status for progress.",
                Status = StatusCodes.Status409Conflict
            });
        }

        // Validate import directory exists before starting
        if (!Directory.Exists(_seeder.SnapshotDirectory))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Import Directory Not Found",
                Detail = $"Could not find RF2 files at: {_seeder.SnapshotDirectory}",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var isResume = existingStatus is not null && existingStatus.IsPaused && !forceRestart;

        _logger.LogInformation(
            "Starting SNOMED CT seeding in background from {Directory}, ActiveOnly: {ActiveOnly}, BatchSize: {BatchSize}, ForceRestart: {ForceRestart}, Resume: {IsResume}",
            _seeder.SnapshotDirectory, activeOnly, batchSize, forceRestart, isResume);

        var options = new SnomedSeedOptions(
            BatchSize: batchSize,
            ActiveOnly: activeOnly,
            VerifyAfterSeed: true);

        // Start seeding in background with its own DI scope
        _ = Task.Run(async () =>
        {
            // Create a new scope for the background task
            await using var scope = _scopeFactory.CreateAsyncScope();
            var seeder = scope.ServiceProvider.GetRequiredService<SnomedSeederService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<SnomedController>>();

            try
            {
                logger.LogInformation("Background seeding task started");
                var result = await seeder.SeedAsync(options, forceRestart);

                if (!result.Ok && result.Error != "Paused")
                {
                    logger.LogError("SNOMED CT seeding failed: {Error}", result.Error);
                }
                else if (result.Error == "Paused")
                {
                    logger.LogInformation("SNOMED CT seeding paused: {Concepts} concepts, {Relationships} relationships",
                        result.ConceptsSeeded, result.RelationshipsSeeded);
                }
                else
                {
                    logger.LogInformation("SNOMED CT seeding completed: {Concepts} concepts, {Relationships} relationships in {Duration}",
                        result.ConceptsSeeded, result.RelationshipsSeeded, result.Duration);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SNOMED CT seeding failed with exception");
            }
        });

        return Accepted(new SnomedSeedStartedResponse(
            Message: isResume ? "Seeding resumed in background" : "Seeding started in background",
            IsResume: isResume,
            SnapshotDirectory: _seeder.SnapshotDirectory,
            StatusEndpoint: "/api/snomed/status",
            JobEndpoint: "/api/snomed/job"));
    }

    /// <summary>
    /// Requests the current seeding job to pause at the next safe checkpoint.
    /// The job can be resumed by calling POST /api/snomed/seed again.
    /// </summary>
    [HttpPost("pause")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult Pause()
    {
        var status = _seeder.GetStatus();
        if (status is null || !status.IsRunning)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No Active Job",
                Detail = "No seeding job is currently running",
                Status = StatusCodes.Status404NotFound
            });
        }

        _logger.LogInformation("Pause requested for seeding job {JobId}", status.JobId);
        _seeder.RequestPause();

        return Ok(new { message = "Pause requested. Job will pause at next checkpoint.", jobId = status.JobId });
    }

    /// <summary>
    /// Resumes a paused or failed seeding job.
    /// Alias for POST /api/snomed/seed (which auto-resumes from checkpoint).
    /// </summary>
    [HttpPost("resume")]
    [ProducesResponseType(typeof(SnomedSeedStartedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult Resume()
    {
        var status = _seeder.GetStatus();
        if (status is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No Job to Resume",
                Detail = "No paused or failed seeding job found",
                Status = StatusCodes.Status404NotFound
            });
        }

        if (!status.IsPaused && !status.IsFailed)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Cannot Resume",
                Detail = $"Job is in state {status.Phase}, which cannot be resumed",
                Status = StatusCodes.Status400BadRequest
            });
        }

        _logger.LogInformation("Resuming seeding job {JobId} from phase {Phase} in background", status.JobId, status.Phase);

        // Start in background with its own DI scope
        _ = Task.Run(async () =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var seeder = scope.ServiceProvider.GetRequiredService<SnomedSeederService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<SnomedController>>();

            try
            {
                var result = await seeder.SeedAsync();
                logger.LogInformation("SNOMED CT seeding resumed and completed: {Concepts} concepts in {Duration}",
                    result.ConceptsSeeded, result.Duration);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SNOMED CT seeding resume failed");
            }
        });

        return Accepted(new SnomedSeedStartedResponse(
            Message: "Seeding resumed in background",
            IsResume: true,
            SnapshotDirectory: _seeder.SnapshotDirectory,
            StatusEndpoint: "/api/snomed/status",
            JobEndpoint: "/api/snomed/job"));
    }

    /// <summary>
    /// Re-seeds SNOMED CT terminology, ignoring any existing checkpoint.
    /// Returns immediately - seeding runs in background.
    /// 
    /// WARNING: This clears any existing checkpoint and starts fresh.
    /// </summary>
    [HttpPost("reseed")]
    [ProducesResponseType(typeof(SnomedSeedStartedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult Reseed(
        [FromQuery] bool activeOnly = true,
        [FromQuery] int batchSize = 1000)
    {
        _logger.LogWarning("Re-seeding SNOMED CT terminology in background - clearing checkpoint and starting fresh");

        // Validate import directory exists
        if (!Directory.Exists(_seeder.SnapshotDirectory))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Import Directory Not Found",
                Detail = $"Could not find RF2 files at: {_seeder.SnapshotDirectory}",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var options = new SnomedSeedOptions(
            BatchSize: batchSize,
            ActiveOnly: activeOnly,
            VerifyAfterSeed: true);

        // Start in background with its own DI scope
        _ = Task.Run(async () =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var seeder = scope.ServiceProvider.GetRequiredService<SnomedSeederService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<SnomedController>>();

            try
            {
                var result = await seeder.SeedAsync(options, forceRestart: true);
                if (result.Ok)
                {
                    logger.LogInformation("SNOMED CT re-seeding completed: {Concepts} concepts in {Duration}",
                        result.ConceptsSeeded, result.Duration);
                }
                else
                {
                    logger.LogError("SNOMED CT re-seeding failed: {Error}", result.Error);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SNOMED CT re-seeding failed with exception");
            }
        });

        return Accepted(new SnomedSeedStartedResponse(
            Message: "Re-seeding started in background (checkpoint cleared)",
            IsResume: false,
            SnapshotDirectory: _seeder.SnapshotDirectory,
            StatusEndpoint: "/api/snomed/status",
            JobEndpoint: "/api/snomed/job"));
    }

    /// <summary>
    /// Clears the seeding checkpoint, allowing a completely fresh start.
    /// Does not delete any already-seeded data.
    /// </summary>
    [HttpDelete("checkpoint")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ClearCheckpoint()
    {
        _logger.LogWarning("Clearing SNOMED CT seeding checkpoint");
        _seeder.ClearCheckpoint();
        return NoContent();
    }

    /// <summary>
    /// Gets the current seeding job details including checkpoint information.
    /// </summary>
    [HttpGet("job")]
    [ProducesResponseType(typeof(SnomedSeedStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult GetJob()
    {
        var status = _seeder.GetStatus();
        if (status is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "No Job Found",
                Detail = "No seeding job checkpoint found",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(status);
    }

    /// <summary>
    /// Verifies SNOMED CT graph integrity.
    /// </summary>
    [HttpGet("verify")]
    [ProducesResponseType(typeof(SnomedSeedVerification), StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(CancellationToken ct)
    {
        var verification = await _seeder.VerifyAsync(ct);
        return Ok(verification);
    }
}

/// <summary>
/// Full status response including seeding job information.
/// </summary>
public sealed record SnomedFullStatusResponse(
    bool IsSeeded,
    long ConceptCount,
    string ImportDirectory,
    string SnapshotDirectory,
    string Message,
    SnomedSeedStatus? SeedingJob);

/// <summary>
/// Response when seeding is started in background.
/// </summary>
public sealed record SnomedSeedStartedResponse(
    string Message,
    bool IsResume,
    string SnapshotDirectory,
    string StatusEndpoint,
    string JobEndpoint);

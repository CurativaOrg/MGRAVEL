using System.Text.Json;
using BLL.Models;
using BLL.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BLL.Services;

/// <summary>
/// Manages checkpoint persistence for resumable SNOMED CT seeding operations.
/// Lazy-initialized: no file I/O until seeding is actually started.
/// Checkpoints are saved to a JSON file in the RF2 directory.
/// </summary>
public class SnomedCheckpointManager
{
    private const string CheckpointFileName = ".snomed-seed-checkpoint.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<SnomedCheckpointManager> _logger;
    private readonly SnomedOptions _options;
    private readonly object _lock = new();

    // Lazy state - only populated when seeding is active
    private SnomedSeedCheckpoint? _currentCheckpoint;
    private string? _checkpointPath;
    private bool _isActive;

    public SnomedCheckpointManager(
        ILogger<SnomedCheckpointManager> logger,
        IOptions<SnomedOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Whether the checkpoint manager is currently active (seeding in progress).
    /// </summary>
    public bool IsActive
    {
        get { lock (_lock) { return _isActive; } }
    }

    /// <summary>
    /// Gets or creates a checkpoint for the specified RF2 directory.
    /// This is the activation point - only called when seeding starts.
    /// </summary>
    public SnomedSeedCheckpoint GetOrCreateCheckpoint(string rf2Directory, SnomedSeedOptions? options = null)
    {
        lock (_lock)
        {
            _isActive = true;
            _checkpointPath = GetCheckpointPath(rf2Directory);

            // Try to load existing checkpoint
            if (File.Exists(_checkpointPath))
            {
                try
                {
                    var json = File.ReadAllText(_checkpointPath);
                    var existing = JsonSerializer.Deserialize<SnomedSeedCheckpoint>(json, JsonOptions);

                    if (existing is not null &&
                        existing.Phase != SnomedSeedPhase.Completed &&
                        existing.Rf2Directory == rf2Directory)
                    {
                        _logger.LogInformation(
                            "Found existing checkpoint for job {JobId} at phase {Phase}, line {Line}",
                            existing.JobId, existing.Phase, existing.LastProcessedLine);

                        _currentCheckpoint = existing;
                        return existing;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load existing checkpoint, creating new one");
                }
            }

            // Create new checkpoint
            _currentCheckpoint = new SnomedSeedCheckpoint
            {
                Rf2Directory = rf2Directory,
                Options = options,
                Phase = SnomedSeedPhase.NotStarted,
                StartedAt = DateTimeOffset.UtcNow
            };

            SaveCheckpoint(_currentCheckpoint);
            return _currentCheckpoint;
        }
    }

    /// <summary>
    /// Updates and persists the checkpoint. No-op if not active.
    /// </summary>
    public void UpdateCheckpoint(Action<SnomedSeedCheckpoint> update)
    {
        lock (_lock)
        {
            if (!_isActive || _currentCheckpoint is null)
                return;

            update(_currentCheckpoint);
            _currentCheckpoint.LastUpdatedAt = DateTimeOffset.UtcNow;
            SaveCheckpoint(_currentCheckpoint);
        }
    }

    /// <summary>
    /// Marks a phase as complete and advances to the next phase.
    /// </summary>
    public void AdvancePhase(SnomedSeedPhase nextPhase)
    {
        UpdateCheckpoint(cp =>
        {
            cp.Phase = nextPhase;
            cp.LastProcessedLine = 0;
            _logger.LogInformation("Advanced to phase {Phase}", nextPhase);
        });
    }

    /// <summary>
    /// Updates progress within current phase.
    /// </summary>
    public void UpdateProgress(long lineNumber, int conceptsSeeded = 0, int descriptionsProcessed = 0, int relationshipsSeeded = 0)
    {
        UpdateCheckpoint(cp =>
        {
            cp.LastProcessedLine = lineNumber;
            if (conceptsSeeded > 0) cp.ConceptsSeeded = conceptsSeeded;
            if (descriptionsProcessed > 0) cp.DescriptionsProcessed = descriptionsProcessed;
            if (relationshipsSeeded > 0) cp.RelationshipsSeeded = relationshipsSeeded;
        });
    }

    /// <summary>
    /// Marks seeding as completed, deletes checkpoint file, and fully deactivates.
    /// </summary>
    public void MarkCompleted(TimeSpan totalElapsed)
    {
        lock (_lock)
        {
            if (!_isActive || _currentCheckpoint is null)
                return;

            _logger.LogInformation("Seeding completed in {Elapsed}", totalElapsed);

            // Delete checkpoint file - no need to resume a completed job
            if (_checkpointPath is not null && File.Exists(_checkpointPath))
            {
                try
                {
                    File.Delete(_checkpointPath);
                    _logger.LogDebug("Deleted checkpoint file after successful completion");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete checkpoint file at {Path}", _checkpointPath);
                }
            }

            // Fully deactivate and release all resources
            _isActive = false;
            _currentCheckpoint = null;
            _checkpointPath = null;
        }
    }

    /// <summary>
    /// Marks seeding as failed.
    /// </summary>
    public void MarkFailed(string error, TimeSpan elapsedSoFar)
    {
        lock (_lock)
        {
            if (!_isActive || _currentCheckpoint is null)
                return;

            _currentCheckpoint.Phase = SnomedSeedPhase.Failed;
            _currentCheckpoint.ErrorMessage = error;
            _currentCheckpoint.ElapsedTime = elapsedSoFar;
            _currentCheckpoint.LastUpdatedAt = DateTimeOffset.UtcNow;
            SaveCheckpoint(_currentCheckpoint);

            _logger.LogError("Seeding failed: {Error}", error);
        }
    }

    /// <summary>
    /// Marks seeding as paused.
    /// </summary>
    public void MarkPaused(TimeSpan elapsedSoFar)
    {
        lock (_lock)
        {
            if (!_isActive || _currentCheckpoint is null)
                return;

            _currentCheckpoint.Phase = SnomedSeedPhase.Paused;
            _currentCheckpoint.ElapsedTime = elapsedSoFar;
            _currentCheckpoint.PauseRequested = false;
            _currentCheckpoint.LastUpdatedAt = DateTimeOffset.UtcNow;
            SaveCheckpoint(_currentCheckpoint);

            _logger.LogInformation("Seeding paused at line {Line}", _currentCheckpoint.LastProcessedLine);

            _isActive = false;
        }
    }

    /// <summary>
    /// Requests a pause at the next safe checkpoint.
    /// </summary>
    public void RequestPause()
    {
        lock (_lock)
        {
            if (_isActive && _currentCheckpoint is not null)
            {
                _currentCheckpoint.PauseRequested = true;
                _logger.LogInformation("Pause requested for job {JobId}", _currentCheckpoint.JobId);
            }
        }
    }

    /// <summary>
    /// Checks if a pause has been requested. Returns false if not active.
    /// </summary>
    public bool IsPauseRequested()
    {
        lock (_lock)
        {
            return _isActive && (_currentCheckpoint?.PauseRequested ?? false);
        }
    }

    /// <summary>
    /// Gets the current in-memory checkpoint (if active).
    /// </summary>
    public SnomedSeedCheckpoint? GetCurrentCheckpoint()
    {
        lock (_lock)
        {
            return _isActive ? _currentCheckpoint : null;
        }
    }

    /// <summary>
    /// Gets the status of the current or last seeding job.
    /// Reads from disk only if needed (lazy).
    /// </summary>
    public SnomedSeedStatus? GetStatus()
    {
        var dir = _options.SnapshotDirectory;

        if (string.IsNullOrEmpty(dir))
            return null;

        var path = GetCheckpointPath(dir);
        SnomedSeedCheckpoint? checkpoint;

        lock (_lock)
        {
            if (_isActive && _currentCheckpoint?.Rf2Directory == dir)
            {
                checkpoint = _currentCheckpoint;
            }
            else if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    checkpoint = JsonSerializer.Deserialize<SnomedSeedCheckpoint>(json, JsonOptions);
                }
                catch
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        if (checkpoint is null)
            return null;

        return new SnomedSeedStatus(
            JobId: checkpoint.JobId,
            Phase: checkpoint.Phase,
            IsRunning: _isActive && checkpoint.Phase is SnomedSeedPhase.Concepts
                or SnomedSeedPhase.Descriptions
                or SnomedSeedPhase.Relationships
                or SnomedSeedPhase.Verification,
            IsPaused: checkpoint.Phase == SnomedSeedPhase.Paused,
            IsCompleted: checkpoint.Phase == SnomedSeedPhase.Completed,
            IsFailed: checkpoint.Phase == SnomedSeedPhase.Failed,
            ConceptsSeeded: checkpoint.ConceptsSeeded,
            DescriptionsProcessed: checkpoint.DescriptionsProcessed,
            RelationshipsSeeded: checkpoint.RelationshipsSeeded,
            ElapsedTime: checkpoint.ElapsedTime,
            ErrorMessage: checkpoint.ErrorMessage,
            StartedAt: checkpoint.StartedAt,
            LastUpdatedAt: checkpoint.LastUpdatedAt);
    }

    /// <summary>
    /// Checks if a checkpoint file exists (without loading it).
    /// </summary>
    public bool HasCheckpoint()
    {
        var dir = _options.SnapshotDirectory;
        if (string.IsNullOrEmpty(dir))
            return false;

        var path = GetCheckpointPath(dir);
        return File.Exists(path);
    }

    /// <summary>
    /// Clears the checkpoint file, forcing a fresh start.
    /// </summary>
    public void ClearCheckpoint()
    {
        var dir = _options.SnapshotDirectory;
        if (string.IsNullOrEmpty(dir))
            return;

        var path = GetCheckpointPath(dir);

        lock (_lock)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Cleared checkpoint at {Path}", path);
            }

            if (_currentCheckpoint?.Rf2Directory == dir)
            {
                _currentCheckpoint = null;
                _checkpointPath = null;
                _isActive = false;
            }
        }
    }

    private void SaveCheckpoint(SnomedSeedCheckpoint checkpoint)
    {
        if (_checkpointPath is null)
            return;

        try
        {
            var dir = Path.GetDirectoryName(_checkpointPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(checkpoint, JsonOptions);
            File.WriteAllText(_checkpointPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save checkpoint to {Path}", _checkpointPath);
        }
    }

    private static string GetCheckpointPath(string rf2Directory)
    {
        var parent = Directory.GetParent(rf2Directory)?.FullName ?? rf2Directory;
        return Path.Combine(parent, CheckpointFileName);
    }
}

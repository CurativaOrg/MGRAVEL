using System.Diagnostics;
using BLL.Models;
using BLL.Options;
using BLL.Utils;
using DAL.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static BLL.Constants;

namespace BLL.Services;

/// <summary>
/// Service for seeding SNOMED CT terminology data from RF2 snapshot files into the graph database.
/// Supports pause/resume and crash recovery via persistent checkpoints.
/// 
/// SNOMED CT RF2 Specification: https://confluence.ihtsdotools.org/display/DOCRELFMT
/// 
/// Seeding phases:
/// 1. Load concepts as SnomedConcept vertices
/// 2. Resolve FSN and preferred terms from descriptions
/// 3. Load relationships as IS_A and DEFINING_REL edges
/// </summary>
public class SnomedSeederService
{
    private readonly IGraphRepository _repo;
    private readonly ILogger<SnomedSeederService> _logger;
    private readonly SnomedOptions _options;
    private readonly SnomedCheckpointManager _checkpointManager;

    public SnomedSeederService(
        IGraphRepository repo,
        ILogger<SnomedSeederService> logger,
        IOptions<SnomedOptions> options,
        SnomedCheckpointManager checkpointManager)
    {
        _repo = repo;
        _logger = logger;
        _options = options.Value;
        _checkpointManager = checkpointManager;
    }

    /// <summary>
    /// Seeds the graph with SNOMED CT concepts from the RF2 snapshot.
    /// Uses the configured import directory (snomed-data/import/Snapshot).
    /// Supports resume from checkpoint after pause or crash.
    /// </summary>
    /// <param name="options">Seeding options (uses defaults from config if null).</param>
    /// <param name="forceRestart">If true, ignores existing checkpoint and starts fresh.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Seeding result with counts and any errors.</returns>
    public async Task<SnomedSeedResult> SeedAsync(
        SnomedSeedOptions? options = null,
        bool forceRestart = false,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var dir = _options.SnapshotDirectory;
        var opts = options ?? new SnomedSeedOptions(
            BatchSize: _options.BatchSize,
            ActiveOnly: _options.ActiveOnly,
            DialectRefsetId: _options.DialectRefsetId);

        // Handle checkpoint
        if (forceRestart)
        {
            _checkpointManager.ClearCheckpoint();
        }

        var checkpoint = _checkpointManager.GetOrCreateCheckpoint(dir, opts);

        // Add previously elapsed time if resuming
        var previousElapsed = checkpoint.ElapsedTime;

        _logger.LogInformation(
            "Starting SNOMED CT seeding from {Directory}, JobId={JobId}, ResumePhase={Phase}, ResumeFromLine={Line}",
            dir, checkpoint.JobId, checkpoint.Phase, checkpoint.LastProcessedLine);

        try
        {
            // Find RF2 files
            var files = Rf2Parser.FindRf2Files(dir);
            if (files is null)
            {
                var error = $"Could not find RF2 files in directory: {dir}";
                _checkpointManager.MarkFailed(error, stopwatch.Elapsed + previousElapsed);
                return new SnomedSeedResult(
                    Ok: false,
                    Error: error,
                    ConceptsSeeded: checkpoint.ConceptsSeeded,
                    DescriptionsProcessed: checkpoint.DescriptionsProcessed,
                    RelationshipsSeeded: checkpoint.RelationshipsSeeded,
                    Duration: stopwatch.Elapsed + previousElapsed);
            }

            _logger.LogInformation("Found RF2 files: Concepts={ConceptFile}, Descriptions={DescFile}, Relationships={RelFile}",
                Path.GetFileName(files.ConceptFile),
                Path.GetFileName(files.DescriptionFile),
                Path.GetFileName(files.RelationshipFile));

            // Determine where to resume
            var startPhase = checkpoint.Phase switch
            {
                SnomedSeedPhase.NotStarted => SnomedSeedPhase.Concepts,
                SnomedSeedPhase.Paused => GetPhaseFromCheckpoint(checkpoint),
                SnomedSeedPhase.Failed => GetPhaseFromCheckpoint(checkpoint),
                _ => checkpoint.Phase
            };

            // Phase 1: Seed concepts
            if (startPhase <= SnomedSeedPhase.Concepts)
            {
                _checkpointManager.AdvancePhase(SnomedSeedPhase.Concepts);

                var resumeFromLine = checkpoint.Phase == SnomedSeedPhase.Concepts
                    ? checkpoint.LastProcessedLine
                    : 0;

                var (conceptsSeeded, paused) = await SeedConceptsWithCheckpointAsync(
                    files.ConceptFile,
                    opts.BatchSize,
                    opts.ActiveOnly,
                    resumeFromLine,
                    ct);

                checkpoint.ConceptsSeeded += conceptsSeeded;

                if (paused)
                {
                    _checkpointManager.MarkPaused(stopwatch.Elapsed + previousElapsed);
                    return CreatePausedResult(checkpoint, stopwatch.Elapsed + previousElapsed);
                }

                _logger.LogInformation("Phase 1 complete: {ConceptCount} concepts seeded", checkpoint.ConceptsSeeded);
            }

            // Phase 2: Resolve descriptions (FSN and preferred terms)
            if (startPhase <= SnomedSeedPhase.Descriptions)
            {
                _checkpointManager.AdvancePhase(SnomedSeedPhase.Descriptions);

                var (descriptionsProcessed, paused) = await SeedDescriptionsWithCheckpointAsync(
                    files.DescriptionFile,
                    files.LanguageRefsetFile,
                    opts.DialectRefsetId,
                    opts.ActiveOnly,
                    ct);

                checkpoint.DescriptionsProcessed = descriptionsProcessed;

                if (paused)
                {
                    _checkpointManager.MarkPaused(stopwatch.Elapsed + previousElapsed);
                    return CreatePausedResult(checkpoint, stopwatch.Elapsed + previousElapsed);
                }

                _logger.LogInformation("Phase 2 complete: {DescCount} descriptions processed", descriptionsProcessed);
            }

            // Phase 3: Seed relationships
            if (startPhase <= SnomedSeedPhase.Relationships)
            {
                _checkpointManager.AdvancePhase(SnomedSeedPhase.Relationships);

                var resumeFromLine = checkpoint.Phase == SnomedSeedPhase.Relationships
                    ? checkpoint.LastProcessedLine
                    : 0;

                var (relationshipsSeeded, paused) = await SeedRelationshipsWithCheckpointAsync(
                    files.RelationshipFile,
                    opts.BatchSize,
                    opts.ActiveOnly,
                    resumeFromLine,
                    ct);

                checkpoint.RelationshipsSeeded += relationshipsSeeded;

                if (paused)
                {
                    _checkpointManager.MarkPaused(stopwatch.Elapsed + previousElapsed);
                    return CreatePausedResult(checkpoint, stopwatch.Elapsed + previousElapsed);
                }

                _logger.LogInformation("Phase 3 complete: {RelCount} relationships seeded", checkpoint.RelationshipsSeeded);
            }

            stopwatch.Stop();
            var totalDuration = stopwatch.Elapsed + previousElapsed;

            // Phase 4: Verify if requested
            if (opts.VerifyAfterSeed)
            {
                _checkpointManager.AdvancePhase(SnomedSeedPhase.Verification);

                var verification = await VerifyAsync(ct);
                if (verification.Errors.Count > 0)
                {
                    _logger.LogWarning("Verification found {ErrorCount} issues: {Errors}",
                        verification.Errors.Count, string.Join("; ", verification.Errors));
                }
                else
                {
                    _logger.LogInformation("Verification passed: {TotalConcepts} concepts, {TotalRels} relationships",
                        verification.TotalConcepts, verification.TotalRelationships);
                }
            }

            _checkpointManager.MarkCompleted(totalDuration);
            _logger.LogInformation("SNOMED CT seeding completed in {Duration}", totalDuration);

            return new SnomedSeedResult(
                Ok: true,
                Error: null,
                ConceptsSeeded: checkpoint.ConceptsSeeded,
                DescriptionsProcessed: checkpoint.DescriptionsProcessed,
                RelationshipsSeeded: checkpoint.RelationshipsSeeded,
                Duration: totalDuration);
        }
        catch (OperationCanceledException)
        {
            _checkpointManager.MarkPaused(stopwatch.Elapsed + previousElapsed);
            _logger.LogWarning("SNOMED CT seeding was cancelled/paused at line {Line}", checkpoint.LastProcessedLine);
            return CreatePausedResult(checkpoint, stopwatch.Elapsed + previousElapsed);
        }
        catch (Exception ex)
        {
            _checkpointManager.MarkFailed(ex.Message, stopwatch.Elapsed + previousElapsed);
            _logger.LogError(ex, "SNOMED CT seeding failed");
            return new SnomedSeedResult(
                Ok: false,
                Error: ex.Message,
                ConceptsSeeded: checkpoint.ConceptsSeeded,
                DescriptionsProcessed: checkpoint.DescriptionsProcessed,
                RelationshipsSeeded: checkpoint.RelationshipsSeeded,
                Duration: stopwatch.Elapsed + previousElapsed);
        }
    }

    /// <summary>
    /// Seeds concepts from the RF2 concept file with checkpoint support.
    /// </summary>
    private async Task<(int seeded, bool paused)> SeedConceptsWithCheckpointAsync(
        string conceptFilePath,
        int batchSize,
        bool activeOnly,
        long resumeFromLine,
        CancellationToken ct)
    {
        _logger.LogInformation("Seeding concepts from {File}, resuming from line {Line}, file exists: {Exists}",
            Path.GetFileName(conceptFilePath), resumeFromLine, File.Exists(conceptFilePath));

        if (!File.Exists(conceptFilePath))
        {
            _logger.LogError("Concept file not found: {Path}", conceptFilePath);
            return (0, paused: false);
        }

        var count = 0;
        var lineNumber = 0L;
        var batch = new List<(string conceptId, IDictionary<string, object> props)>(batchSize);
        var checkpointInterval = _options.ProgressLogInterval;

        _logger.LogDebug("Starting to stream concepts from {File}", conceptFilePath);

        await foreach (var concept in Rf2Parser.StreamConceptsAsync(conceptFilePath, ct))
        {
            lineNumber++;

            // Log first few concepts for debugging
            if (lineNumber <= 3)
            {
                _logger.LogDebug("Read concept line {Line}: Id={Id}, Active={Active}",
                    lineNumber, concept.Id, concept.Active);
            }

            // Skip lines until we reach resume point
            if (lineNumber <= resumeFromLine)
                continue;

            // Check for pause request
            if (_checkpointManager.IsPauseRequested())
            {
                // Flush current batch before pausing
                if (batch.Count > 0)
                {
                    await FlushConceptBatchAsync(batch, ct);
                    count += batch.Count;
                }
                _checkpointManager.UpdateProgress(lineNumber, conceptsSeeded: count);
                return (count, paused: true);
            }

            if (activeOnly && !concept.Active)
                continue;

            var props = new Dictionary<string, object>
            {
                [ClinicalProperties.ConceptId] = concept.Id,
                [ClinicalProperties.Active] = concept.Active,
                [ClinicalProperties.ModuleId] = concept.ModuleId,
                [ClinicalProperties.EffectiveTime] = concept.EffectiveTime
            };

            batch.Add((concept.Id, props));
            count++;

            // Log progress at batch boundaries
            if (batch.Count == 1)
            {
                _logger.LogDebug("First concept in batch {BatchNum}: {ConceptId}", (count / batchSize) + 1, concept.Id);
            }

            if (batch.Count >= batchSize)
            {
                _logger.LogDebug("Flushing batch of {Count} concepts to graph", batch.Count);
                await FlushConceptBatchAsync(batch, ct);
                batch.Clear();

                // Update checkpoint after each batch for resume reliability
                _checkpointManager.UpdateProgress(lineNumber, conceptsSeeded: count);

                // Log progress periodically
                if (count % checkpointInterval == 0)
                {
                    _logger.LogInformation("Concepts progress: {Count} loaded at line {Line}", count, lineNumber);
                }
            }
        }

        _logger.LogInformation("Finished streaming concepts. Total lines: {Lines}, Count: {Count}, Remaining in batch: {Remaining}",
            lineNumber, count, batch.Count);

        // Flush remaining
        if (batch.Count > 0)
        {
            _logger.LogDebug("Flushing final batch of {Count} concepts", batch.Count);
            await FlushConceptBatchAsync(batch, ct);
        }

        _checkpointManager.UpdateProgress(lineNumber, conceptsSeeded: count);
        return (count, paused: false);
    }

    /// <summary>
    /// Seeds descriptions with checkpoint support.
    /// Note: Description phase doesn't support fine-grained resume due to in-memory mapping requirement.
    /// </summary>
    private async Task<(int processed, bool paused)> SeedDescriptionsWithCheckpointAsync(
        string descriptionFilePath,
        string? languageRefsetFilePath,
        string dialectRefsetId,
        bool activeOnly,
        CancellationToken ct)
    {
        _logger.LogInformation("Processing descriptions from {File}", Path.GetFileName(descriptionFilePath));

        // Check for pause before starting
        if (_checkpointManager.IsPauseRequested())
            return (0, paused: true);

        // Step 1: Build preferred description ID set from language refset
        var preferredDescriptionIds = new HashSet<string>();
        if (languageRefsetFilePath is not null && File.Exists(languageRefsetFilePath))
        {
            _logger.LogDebug("Loading language refset for preferred term resolution");
            await foreach (var member in Rf2Parser.StreamLanguageRefsetAsync(languageRefsetFilePath, ct))
            {
                if (!member.Active)
                    continue;

                if (member.RefsetId == dialectRefsetId &&
                    member.AcceptabilityId == Snomed.Acceptability.Preferred)
                {
                    preferredDescriptionIds.Add(member.ReferencedComponentId);
                }

                // Allow cancellation
                if (_checkpointManager.IsPauseRequested())
                    return (0, paused: true);
            }
            _logger.LogDebug("Found {Count} preferred descriptions", preferredDescriptionIds.Count);
        }

        // Step 2: Build concept ? descriptions map
        var conceptDescriptions = new Dictionary<string, (string? fsn, string? preferredTerm)>();
        var count = 0;

        await foreach (var desc in Rf2Parser.StreamDescriptionsAsync(descriptionFilePath, ct))
        {
            if (_checkpointManager.IsPauseRequested())
            {
                _checkpointManager.UpdateProgress(0, descriptionsProcessed: count);
                return (count, paused: true);
            }

            if (activeOnly && !desc.Active)
                continue;

            count++;

            if (!conceptDescriptions.TryGetValue(desc.ConceptId, out var existing))
                existing = (null, null);

            // FSN
            if (desc.TypeId == Snomed.DescriptionTypes.Fsn)
            {
                existing = (desc.Term, existing.preferredTerm);
            }
            // Preferred term
            else if (desc.TypeId == Snomed.DescriptionTypes.Synonym &&
                     preferredDescriptionIds.Contains(desc.Id))
            {
                existing = (existing.fsn, desc.Term);
            }

            conceptDescriptions[desc.ConceptId] = existing;

            if (count % _options.ProgressLogInterval == 0)
                _logger.LogDebug("Descriptions progress: {Count} processed", count);
        }

        // Step 3: Update concept vertices
        _logger.LogDebug("Updating {Count} concept vertices with descriptions", conceptDescriptions.Count);
        var updateCount = 0;

        foreach (var (conceptId, (fsn, preferredTerm)) in conceptDescriptions)
        {
            ct.ThrowIfCancellationRequested();

            if (_checkpointManager.IsPauseRequested())
            {
                _checkpointManager.UpdateProgress(0, descriptionsProcessed: count);
                return (count, paused: true);
            }

            var vertexId = await _repo.GetVertexIdByLabelAndPropertyAsync(
                ClinicalLabels.SnomedConcept,
                ClinicalProperties.ConceptId,
                conceptId,
                ct);

            if (vertexId is not null)
            {
                var updateProps = new Dictionary<string, object>();
                if (fsn is not null)
                    updateProps[ClinicalProperties.Fsn] = fsn;
                if (preferredTerm is not null)
                    updateProps[ClinicalProperties.PreferredTerm] = preferredTerm;

                if (updateProps.Count > 0)
                {
                    await _repo.UpdateVertexPropertiesAsync(vertexId, updateProps, ct);
                    updateCount++;
                }
            }

            if (updateCount % _options.ProgressLogInterval == 0 && updateCount > 0)
                _logger.LogDebug("Description updates progress: {Count} concepts updated", updateCount);
        }

        _logger.LogDebug("Updated {UpdateCount} concepts with FSN/preferred terms", updateCount);
        _checkpointManager.UpdateProgress(0, descriptionsProcessed: count);
        return (count, paused: false);
    }

    /// <summary>
    /// Seeds relationships with checkpoint support.
    /// </summary>
    private async Task<(int seeded, bool paused)> SeedRelationshipsWithCheckpointAsync(
        string relationshipFilePath,
        int batchSize,
        bool activeOnly,
        long resumeFromLine,
        CancellationToken ct)
    {
        _logger.LogInformation("Seeding relationships from {File}, resuming from line {Line}",
            Path.GetFileName(relationshipFilePath), resumeFromLine);

        var count = 0;
        var skipped = 0;
        var lineNumber = 0L;
        var checkpointInterval = _options.ProgressLogInterval;

        await foreach (var rel in Rf2Parser.StreamRelationshipsAsync(relationshipFilePath, ct))
        {
            lineNumber++;

            // Skip lines until we reach resume point
            if (lineNumber <= resumeFromLine)
                continue;

            // Check for pause request
            if (_checkpointManager.IsPauseRequested())
            {
                _checkpointManager.UpdateProgress(lineNumber, relationshipsSeeded: count);
                return (count, paused: true);
            }

            ct.ThrowIfCancellationRequested();

            if (activeOnly && !rel.Active)
                continue;

            // Only use inferred relationships
            if (rel.CharacteristicTypeId != Snomed.CharacteristicTypes.Inferred)
                continue;

            // Get source and destination vertex IDs
            var sourceVertexId = await _repo.GetVertexIdByLabelAndPropertyAsync(
                ClinicalLabels.SnomedConcept,
                ClinicalProperties.ConceptId,
                rel.SourceId,
                ct);

            var destVertexId = await _repo.GetVertexIdByLabelAndPropertyAsync(
                ClinicalLabels.SnomedConcept,
                ClinicalProperties.ConceptId,
                rel.DestinationId,
                ct);

            if (sourceVertexId is null || destVertexId is null)
            {
                skipped++;
                continue;
            }

            // Determine edge label
            var edgeLabel = rel.TypeId == Snomed.RelationshipTypes.IsA
                ? ClinicalEdges.IsA
                : ClinicalEdges.DefiningRel;

            // Build edge properties
            IDictionary<string, object>? edgeProps = null;
            if (edgeLabel == ClinicalEdges.DefiningRel)
            {
                edgeProps = new Dictionary<string, object>
                {
                    [ClinicalProperties.RelationshipTypeId] = rel.TypeId
                };
            }

            // Create edge
            await _repo.AddEdgeAsync(edgeLabel, sourceVertexId, destVertexId, edgeProps, ct);
            count++;

            // Update checkpoint periodically
            if (count % checkpointInterval == 0)
            {
                _checkpointManager.UpdateProgress(lineNumber, relationshipsSeeded: count);
                _logger.LogDebug("Relationships progress: {Count} loaded, {Skipped} skipped at line {Line}",
                    count, skipped, lineNumber);
            }
        }

        if (skipped > 0)
            _logger.LogWarning("Skipped {Skipped} relationships due to missing concept vertices", skipped);

        _checkpointManager.UpdateProgress(lineNumber, relationshipsSeeded: count);
        return (count, paused: false);
    }

    /// <summary>
    /// Requests the current seeding job to pause at the next safe checkpoint.
    /// </summary>
    public void RequestPause()
    {
        _checkpointManager.RequestPause();
    }

    /// <summary>
    /// Gets the current seeding status.
    /// </summary>
    public SnomedSeedStatus? GetStatus()
    {
        return _checkpointManager.GetStatus();
    }

    /// <summary>
    /// Clears the checkpoint, allowing a fresh start.
    /// </summary>
    public void ClearCheckpoint()
    {
        _checkpointManager.ClearCheckpoint();
    }

    /// <summary>
    /// Gets the configured import directory path.
    /// </summary>
    public string ImportDirectory => _options.ImportDirectory;

    /// <summary>
    /// Gets the configured snapshot directory path.
    /// </summary>
    public string SnapshotDirectory => _options.SnapshotDirectory;

    /// <summary>
    /// Verifies graph integrity after seeding.
    /// </summary>
    public async Task<SnomedSeedVerification> VerifyAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Verifying SNOMED CT graph integrity");

        var errors = new List<string>();

        var totalConcepts = await _repo.CountVerticesByLabelAsync(ClinicalLabels.SnomedConcept, ct: ct);

        var activeConcepts = await _repo.CountVerticesByLabelAsync(
            ClinicalLabels.SnomedConcept,
            new Dictionary<string, object> { [ClinicalProperties.Active] = true },
            ct);

        var rootVertex = await _repo.GetVertexByLabelAndPropertyAsync(
            ClinicalLabels.SnomedConcept,
            ClinicalProperties.ConceptId,
            Snomed.Hierarchies.Root,
            ct);
        var rootExists = rootVertex is not null;

        if (!rootExists)
            errors.Add($"Root concept {Snomed.Hierarchies.Root} not found");

        var clinicalFindingVertex = await _repo.GetVertexByLabelAndPropertyAsync(
            ClinicalLabels.SnomedConcept,
            ClinicalProperties.ConceptId,
            Snomed.Hierarchies.ClinicalFinding,
            ct);

        if (clinicalFindingVertex is null)
            errors.Add($"Clinical Finding concept {Snomed.Hierarchies.ClinicalFinding} not found");

        _logger.LogInformation("Verification complete: {TotalConcepts} concepts ({ActiveConcepts} active), Root exists: {RootExists}",
            totalConcepts, activeConcepts, rootExists);

        return new SnomedSeedVerification(
            TotalConcepts: totalConcepts,
            ActiveConcepts: activeConcepts,
            TotalRelationships: 0,
            IsARelationships: 0,
            RootConceptExists: rootExists,
            Errors: errors);
    }

    /// <summary>
    /// Checks if SNOMED CT data has already been seeded.
    /// </summary>
    public async Task<bool> IsSeededAsync(CancellationToken ct = default)
    {
        var count = await _repo.CountVerticesByLabelAsync(ClinicalLabels.SnomedConcept, ct: ct);
        return count > 0;
    }

    /// <summary>
    /// Gets the count of seeded concepts.
    /// </summary>
    public async Task<long> GetConceptCountAsync(CancellationToken ct = default)
    {
        return await _repo.CountVerticesByLabelAsync(ClinicalLabels.SnomedConcept, ct: ct);
    }

    /// <summary>
    /// Maximum concurrent graph operations per batch.
    /// </summary>
    private const int MaxConcurrency = 16;

    private async Task FlushConceptBatchAsync(
        List<(string conceptId, IDictionary<string, object> props)> batch,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var tasks = new List<Task>(batch.Count);

        foreach (var (conceptId, props) in batch)
        {
            ct.ThrowIfCancellationRequested();

            await semaphore.WaitAsync(ct);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await _repo.UpsertVertexAndReturnIdAsync(
                        ClinicalLabels.SnomedConcept,
                        ClinicalProperties.ConceptId,
                        conceptId,
                        props,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upsert concept {ConceptId}", conceptId);
                    throw;
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        sw.Stop();
        _logger.LogDebug("Flushed {Count} concepts in {Elapsed}ms ({Rate:F1}/sec)",
            batch.Count, sw.ElapsedMilliseconds,
            batch.Count / Math.Max(sw.Elapsed.TotalSeconds, 0.001));
    }

    private static SnomedSeedPhase GetPhaseFromCheckpoint(SnomedSeedCheckpoint checkpoint)
    {
        if (checkpoint.RelationshipsSeeded > 0)
            return SnomedSeedPhase.Relationships;
        if (checkpoint.DescriptionsProcessed > 0)
            return SnomedSeedPhase.Descriptions;
        if (checkpoint.ConceptsSeeded > 0)
            return SnomedSeedPhase.Concepts;
        return SnomedSeedPhase.Concepts;
    }

    private static SnomedSeedResult CreatePausedResult(SnomedSeedCheckpoint checkpoint, TimeSpan elapsed)
    {
        return new SnomedSeedResult(
            Ok: true,
            Error: "Paused",
            ConceptsSeeded: checkpoint.ConceptsSeeded,
            DescriptionsProcessed: checkpoint.DescriptionsProcessed,
            RelationshipsSeeded: checkpoint.RelationshipsSeeded,
            Duration: elapsed);
    }
}

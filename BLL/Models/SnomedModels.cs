namespace BLL.Models;

/// <summary>
/// Options for SNOMED CT seeding operations.
/// </summary>
public sealed record SnomedSeedOptions(
    int BatchSize = 1000,
    bool ActiveOnly = true,
    string DialectRefsetId = "900000000000509007",
    bool CreateIndexesFirst = true,
    bool VerifyAfterSeed = true);

/// <summary>
/// Result of a SNOMED CT seeding operation.
/// </summary>
public sealed record SnomedSeedResult(
    bool Ok,
    string? Error,
    int ConceptsSeeded,
    int DescriptionsProcessed,
    int RelationshipsSeeded,
    TimeSpan Duration);

/// <summary>
/// Phases of SNOMED CT seeding.
/// </summary>
public enum SnomedSeedPhase
{
    NotStarted = 0,
    Concepts = 1,
    Descriptions = 2,
    Relationships = 3,
    Verification = 4,
    Completed = 5,
    Paused = 6,
    Failed = 7
}

/// <summary>
/// Checkpoint for resumable SNOMED CT seeding.
/// </summary>
public sealed class SnomedSeedCheckpoint
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public SnomedSeedPhase Phase { get; set; } = SnomedSeedPhase.NotStarted;
    public string Rf2Directory { get; set; } = string.Empty;
    public long LastProcessedLine { get; set; }
    public string? LastConceptId { get; set; }
    public int ConceptsSeeded { get; set; }
    public int DescriptionsProcessed { get; set; }
    public int RelationshipsSeeded { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public TimeSpan ElapsedTime { get; set; }
    public string? ErrorMessage { get; set; }
    public bool PauseRequested { get; set; }
    public SnomedSeedOptions? Options { get; set; }
}

/// <summary>
/// Status of a SNOMED CT seeding job.
/// </summary>
public sealed record SnomedSeedStatus(
    string JobId,
    SnomedSeedPhase Phase,
    bool IsRunning,
    bool IsPaused,
    bool IsCompleted,
    bool IsFailed,
    int ConceptsSeeded,
    int DescriptionsProcessed,
    int RelationshipsSeeded,
    TimeSpan ElapsedTime,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUpdatedAt);

/// <summary>
/// Result of SNOMED CT graph verification.
/// </summary>
public sealed record SnomedSeedVerification(
    long TotalConcepts,
    long ActiveConcepts,
    long TotalRelationships,
    long IsARelationships,
    bool RootConceptExists,
    IReadOnlyList<string> Errors);

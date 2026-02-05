namespace BLL.Options;

/// <summary>
/// Configuration options for SNOMED CT terminology services.
/// Bind to "Snomed" section in appsettings.json.
/// </summary>
public sealed class SnomedOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Snomed";

    /// <summary>
    /// Default import directory name (relative to application root).
    /// </summary>
    public const string DefaultImportDirectory = "snomed-data/import";

    /// <summary>
    /// Path to the SNOMED CT import directory containing unpacked RF2 files.
    /// Expected structure: {ImportDirectory}/Snapshot/Terminology/*.txt
    /// Default: "snomed-data/import"
    /// </summary>
    public string ImportDirectory { get; set; } = DefaultImportDirectory;

    /// <summary>
    /// Gets the full path to the Snapshot directory within the import directory.
    /// </summary>
    public string SnapshotDirectory => Path.Combine(ImportDirectory, "Snapshot");

    /// <summary>
    /// SNOMED CT version identifier (YYYYMMDD format from RF2 effective date).
    /// </summary>
    public string TerminologyVersion { get; set; } = string.Empty;

    /// <summary>
    /// Language refset ID for preferred term resolution.
    /// Default: US English (900000000000509007).
    /// </summary>
    public string DialectRefsetId { get; set; } = Constants.Snomed.LanguageRefsets.UsEnglish;

    /// <summary>
    /// Whether to load only active concepts and relationships.
    /// Default: true.
    /// </summary>
    public bool ActiveOnly { get; set; } = true;

    /// <summary>
    /// Batch size for bulk graph operations during seeding.
    /// Default: 1000.
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Whether to enable semantic normalization during FHIR resource persistence.
    /// Default: true.
    /// </summary>
    public bool EnableSemanticNormalization { get; set; } = true;

    /// <summary>
    /// Progress logging interval (number of records between progress logs).
    /// Default: 10000.
    /// </summary>
    public int ProgressLogInterval { get; set; } = 10000;
}

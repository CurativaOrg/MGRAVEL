using System.Runtime.CompilerServices;

namespace BLL.Utils;

/// <summary>
/// Low-level utilities for parsing SNOMED CT RF2 distribution files.
/// RF2 files are tab-delimited with a header row.
/// 
/// Per SNOMED CT RF2 Specification:
/// https://confluence.ihtsdotools.org/display/DOCRELFMT
/// </summary>
public static class Rf2Parser
{
    private const char TabDelimiter = '\t';

    /// <summary>
    /// Parses a concept row from sct2_Concept_Snapshot file.
    /// Format: id, effectiveTime, active, moduleId, definitionStatusId
    /// </summary>
    public static Rf2Concept? ParseConceptRow(string line)
    {
        var parts = line.Split(TabDelimiter);
        if (parts.Length < 5)
            return null;

        if (!int.TryParse(parts[2], out var activeInt))
            return null;

        return new Rf2Concept(
            Id: parts[0],
            EffectiveTime: parts[1],
            Active: activeInt == 1,
            ModuleId: parts[3],
            DefinitionStatusId: parts[4]);
    }

    /// <summary>
    /// Parses a description row from sct2_Description_Snapshot file.
    /// Format: id, effectiveTime, active, moduleId, conceptId, languageCode, typeId, term, caseSignificanceId
    /// </summary>
    public static Rf2Description? ParseDescriptionRow(string line)
    {
        var parts = line.Split(TabDelimiter);
        if (parts.Length < 9)
            return null;

        if (!int.TryParse(parts[2], out var activeInt))
            return null;

        return new Rf2Description(
            Id: parts[0],
            EffectiveTime: parts[1],
            Active: activeInt == 1,
            ModuleId: parts[3],
            ConceptId: parts[4],
            LanguageCode: parts[5],
            TypeId: parts[6],
            Term: parts[7],
            CaseSignificanceId: parts[8]);
    }

    /// <summary>
    /// Parses a relationship row from sct2_Relationship_Snapshot file.
    /// Format: id, effectiveTime, active, moduleId, sourceId, destinationId, relationshipGroup, typeId, characteristicTypeId, modifierId
    /// </summary>
    public static Rf2Relationship? ParseRelationshipRow(string line)
    {
        var parts = line.Split(TabDelimiter);
        if (parts.Length < 10)
            return null;

        if (!int.TryParse(parts[2], out var activeInt))
            return null;

        if (!int.TryParse(parts[6], out var relationshipGroup))
            relationshipGroup = 0;

        return new Rf2Relationship(
            Id: parts[0],
            EffectiveTime: parts[1],
            Active: activeInt == 1,
            ModuleId: parts[3],
            SourceId: parts[4],
            DestinationId: parts[5],
            RelationshipGroup: relationshipGroup,
            TypeId: parts[7],
            CharacteristicTypeId: parts[8],
            ModifierId: parts[9]);
    }

    /// <summary>
    /// Parses a language refset row from der2_cRefset_LanguageSnapshot file.
    /// Format: id, effectiveTime, active, moduleId, refsetId, referencedComponentId, acceptabilityId
    /// </summary>
    public static Rf2LanguageRefsetMember? ParseLanguageRefsetRow(string line)
    {
        var parts = line.Split(TabDelimiter);
        if (parts.Length < 7)
            return null;

        if (!int.TryParse(parts[2], out var activeInt))
            return null;

        return new Rf2LanguageRefsetMember(
            Id: parts[0],
            EffectiveTime: parts[1],
            Active: activeInt == 1,
            ModuleId: parts[3],
            RefsetId: parts[4],
            ReferencedComponentId: parts[5],
            AcceptabilityId: parts[6]);
    }

    /// <summary>
    /// Streams and parses concept rows from an RF2 concept file.
    /// Skips the header row automatically.
    /// </summary>
    public static async IAsyncEnumerable<Rf2Concept> StreamConceptsAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
        using var reader = new StreamReader(stream);

        // Skip header
        await reader.ReadLineAsync(ct);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var concept = ParseConceptRow(line);
            if (concept is not null)
                yield return concept;
        }
    }

    /// <summary>
    /// Streams and parses description rows from an RF2 description file.
    /// Skips the header row automatically.
    /// </summary>
    public static async IAsyncEnumerable<Rf2Description> StreamDescriptionsAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
        using var reader = new StreamReader(stream);

        // Skip header
        await reader.ReadLineAsync(ct);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var description = ParseDescriptionRow(line);
            if (description is not null)
                yield return description;
        }
    }

    /// <summary>
    /// Streams and parses relationship rows from an RF2 relationship file.
    /// Skips the header row automatically.
    /// </summary>
    public static async IAsyncEnumerable<Rf2Relationship> StreamRelationshipsAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
        using var reader = new StreamReader(stream);

        // Skip header
        await reader.ReadLineAsync(ct);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var relationship = ParseRelationshipRow(line);
            if (relationship is not null)
                yield return relationship;
        }
    }

    /// <summary>
    /// Streams and parses language refset rows from an RF2 language refset file.
    /// Skips the header row automatically.
    /// </summary>
    public static async IAsyncEnumerable<Rf2LanguageRefsetMember> StreamLanguageRefsetAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
        using var reader = new StreamReader(stream);

        // Skip header
        await reader.ReadLineAsync(ct);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var member = ParseLanguageRefsetRow(line);
            if (member is not null)
                yield return member;
        }
    }

    /// <summary>
    /// Finds RF2 files in a snapshot directory by pattern matching.
    /// </summary>
    public static Rf2FileSet? FindRf2Files(string snapshotDirectory)
    {
        if (!Directory.Exists(snapshotDirectory))
            return null;

        var terminologyDir = Path.Combine(snapshotDirectory, "Terminology");
        var languageDir = Path.Combine(snapshotDirectory, "Refset", "Language");

        string? conceptFile = null;
        string? descriptionFile = null;
        string? relationshipFile = null;
        string? languageRefsetFile = null;

        if (Directory.Exists(terminologyDir))
        {
            var files = Directory.GetFiles(terminologyDir, "*.txt");
            conceptFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("sct2_Concept_Snapshot"));
            descriptionFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("sct2_Description_Snapshot"));
            relationshipFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("sct2_Relationship_Snapshot"));
        }

        if (Directory.Exists(languageDir))
        {
            var files = Directory.GetFiles(languageDir, "*.txt");
            languageRefsetFile = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("der2_cRefset_LanguageSnapshot"));
        }

        if (conceptFile is null || descriptionFile is null || relationshipFile is null)
            return null;

        return new Rf2FileSet(
            ConceptFile: conceptFile,
            DescriptionFile: descriptionFile,
            RelationshipFile: relationshipFile,
            LanguageRefsetFile: languageRefsetFile);
    }
}

/// <summary>
/// RF2 concept record from sct2_Concept_Snapshot file.
/// </summary>
public sealed record Rf2Concept(
    string Id,
    string EffectiveTime,
    bool Active,
    string ModuleId,
    string DefinitionStatusId);

/// <summary>
/// RF2 description record from sct2_Description_Snapshot file.
/// </summary>
public sealed record Rf2Description(
    string Id,
    string EffectiveTime,
    bool Active,
    string ModuleId,
    string ConceptId,
    string LanguageCode,
    string TypeId,
    string Term,
    string CaseSignificanceId);

/// <summary>
/// RF2 relationship record from sct2_Relationship_Snapshot file.
/// </summary>
public sealed record Rf2Relationship(
    string Id,
    string EffectiveTime,
    bool Active,
    string ModuleId,
    string SourceId,
    string DestinationId,
    int RelationshipGroup,
    string TypeId,
    string CharacteristicTypeId,
    string ModifierId);

/// <summary>
/// RF2 language refset member record from der2_cRefset_LanguageSnapshot file.
/// </summary>
public sealed record Rf2LanguageRefsetMember(
    string Id,
    string EffectiveTime,
    bool Active,
    string ModuleId,
    string RefsetId,
    string ReferencedComponentId,
    string AcceptabilityId);

/// <summary>
/// Set of RF2 files found in a snapshot directory.
/// </summary>
public sealed record Rf2FileSet(
    string ConceptFile,
    string DescriptionFile,
    string RelationshipFile,
    string? LanguageRefsetFile);

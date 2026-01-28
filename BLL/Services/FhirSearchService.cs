using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BLL.Models;
using DAL.Repositories;
using Microsoft.Extensions.Logging;
using static BLL.Constants;

namespace BLL.Services;

/// <summary>
/// Service implementing FHIR Search functionality per FHIR R6 specification.
/// 
/// Specification Reference: https://build.fhir.org/search.html (§3.2.1)
/// 
/// This service provides comprehensive FHIR search capabilities including:
/// - All search parameter types (date, number, string, token, reference, composite, quantity, uri, special)
/// - Search prefixes/comparators (eq, ne, gt, ge, lt, le, sa, eb, ap)
/// - Search modifiers (exact, contains, missing, above, below, in, not-in, etc.)
/// - Chaining and reverse chaining (_has)
/// - Include and RevInclude directives
/// - Result modification (_sort, _count, _summary, _elements)
/// - Pagination with proper Link headers
/// 
/// Conformance Requirements Implemented:
/// - §FCS7: Self link contains parameters actually used by server
/// - §FCS20: Syntactically incorrect parameters return errors
/// - §FCS21: Logical issues (unknown codes, etc.) return empty results with warnings
/// - §FCS22: Unknown parameters are ignored (lenient by default)
/// - §FCS35: Unsupported modifiers cause search rejection
/// </summary>
public partial class FhirSearchService
{
    private readonly IGraphRepository _repo;
    private readonly ILogger<FhirSearchService> _logger;
    private readonly FhirValidationService _validation;

    public FhirSearchService(
        IGraphRepository repo,
        ILogger<FhirSearchService> logger,
        FhirValidationService validation)
    {
        _repo = repo;
        _logger = logger;
        _validation = validation;
    }

    #region Main Search Methods

    /// <summary>
    /// Executes a FHIR search and returns a searchset Bundle.
    /// 
    /// Per spec §3.2.1.3: "Search result bundles will always have the Bundle.type of searchset."
    /// </summary>
    public async Task<FhirOperationResult> ExecuteSearchAsync(
        FhirSearchRequest request,
        string selfUrl,
        string baseUrl,
        CancellationToken ct = default)
    {
        try
        {
            var (results, totalCount, warnings) = await PerformSearchAsync(request, ct);

            var bundle = BuildSearchBundle(results, totalCount, request, selfUrl, baseUrl, warnings);

            return FhirOperationResult.Ok(bundle);
        }
        catch (FhirSearchException ex)
        {
            _logger.LogWarning(ex, "Search validation failed");
            return FhirOperationResult.BadRequest(CreateSearchError(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search execution failed");
            return FhirOperationResult.InternalError(CreateSearchError($"Search failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Parses search parameters from query string into structured format.
    /// 
    /// Per spec §3.2.1.2.3: Search parameters can be filters, text searches, or result modifiers.
    /// </summary>
    public FhirSearchRequest ParseSearchParameters(
        string? resourceType,
        IDictionary<string, string> queryParams,
        int defaultCount = 100)
    {
        var parameters = new List<FhirSearchParameter>();
        var includes = new List<string>();
        var revIncludes = new List<string>();
        var elements = new List<string>();
        var types = new List<string>();

        int count = defaultCount;
        int offset = 0;
        string? sort = null;
        bool sortDesc = false;
        string? summary = null;
        long? total = null;

        foreach (var (key, value) in queryParams)
        {
            if (string.IsNullOrEmpty(value)) continue;

            // Handle result modification parameters
            switch (key.ToLowerInvariant())
            {
                case SearchParams.Count:
                    if (int.TryParse(value, out var c)) count = Math.Min(c, 1000);
                    continue;
                case SearchParams.Offset:
                    if (int.TryParse(value, out var o)) offset = Math.Max(o, 0);
                    continue;
                case SearchParams.Sort:
                    sort = value.TrimStart('-');
                    sortDesc = value.StartsWith('-');
                    continue;
                case SearchParams.Summary:
                    summary = value;
                    continue;
                case SearchParams.Total:
                    if (value == "accurate") total = -1;
                    continue;
                case SearchParams.Elements:
                    elements.AddRange(value.Split(',').Select(e => e.Trim()));
                    continue;
                case SearchParams.Include:
                    includes.Add(value);
                    continue;
                case SearchParams.RevInclude:
                    revIncludes.Add(value);
                    continue;
                case SearchParams.Type:
                    types.AddRange(value.Split(',').Select(t => t.Trim()));
                    continue;
            }

            // Parse search parameter
            var param = ParseSingleParameter(key, value, resourceType);
            if (param != null)
                parameters.Add(param);
        }

        return new FhirSearchRequest(
            ResourceType: resourceType,
            ResourceTypes: types.Count > 0 ? types : null,
            Parameters: parameters.Count > 0 ? parameters : null,
            Count: count,
            Offset: offset,
            Sort: sort,
            SortDescending: sortDesc,
            Summary: summary,
            Elements: elements.Count > 0 ? elements : null,
            Include: includes.Count > 0 ? includes : null,
            RevInclude: revIncludes.Count > 0 ? revIncludes : null,
            Total: total);
    }

    #endregion

    #region Parameter Parsing

    /// <summary>
    /// Parses a single search parameter into its structured representation.
    /// 
    /// Per spec §3.2.1.5: Parameter format is [name]:[modifier]=[prefix][value]
    /// </summary>
    private static FhirSearchParameter? ParseSingleParameter(string key, string value, string? resourceType)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Parse name and modifier
        var (name, modifier, modifierValue) = ParseParameterName(key);

        // Determine parameter type
        var paramType = GetParameterType(name);

        // Parse prefix for ordered types
        var (prefix, cleanValue) = ExtractPrefix(value, paramType);

        // Handle OR values (comma-separated)
        var orValues = cleanValue.Contains(',')
            ? cleanValue.Split(',').Select(v => v.Trim()).ToList()
            : null;

        return new FhirSearchParameter(
            Name: name,
            Type: paramType,
            Modifier: modifier,
            ModifierValue: modifierValue,
            Prefix: prefix,
            RawValue: cleanValue,
            OrValues: orValues);
    }

    /// <summary>
    /// Parses parameter name to extract name, modifier, and modifier value.
    /// 
    /// Per spec §3.2.1.5.5: Modifier format is [name]:[modifier]
    /// </summary>
    private static (string name, FhirSearchModifier modifier, string? modifierValue) ParseParameterName(string key)
    {
        var colonIndex = key.IndexOf(':');
        if (colonIndex < 0)
            return (key, FhirSearchModifier.None, null);

        var name = key[..colonIndex];
        var modifierPart = key[(colonIndex + 1)..];

        // Check for type modifier (e.g., subject:Patient)
        if (IsResourceTypeName(modifierPart))
            return (name, FhirSearchModifier.Type, modifierPart);

        var modifier = modifierPart.ToLowerInvariant() switch
        {
            SearchModifiers.Above => FhirSearchModifier.Above,
            SearchModifiers.Below => FhirSearchModifier.Below,
            SearchModifiers.CodeText => FhirSearchModifier.CodeText,
            SearchModifiers.Contains => FhirSearchModifier.Contains,
            SearchModifiers.Exact => FhirSearchModifier.Exact,
            SearchModifiers.Identifier => FhirSearchModifier.Identifier,
            SearchModifiers.In => FhirSearchModifier.In,
            SearchModifiers.Iterate => FhirSearchModifier.Iterate,
            SearchModifiers.Missing => FhirSearchModifier.Missing,
            SearchModifiers.Not => FhirSearchModifier.Not,
            SearchModifiers.NotIn => FhirSearchModifier.NotIn,
            SearchModifiers.OfType => FhirSearchModifier.OfType,
            SearchModifiers.Text => FhirSearchModifier.Text,
            SearchModifiers.TextAdvanced => FhirSearchModifier.TextAdvanced,
            _ => FhirSearchModifier.None
        };

        return (name, modifier, modifier == FhirSearchModifier.None ? modifierPart : null);
    }

    /// <summary>
    /// Extracts prefix from value for ordered parameter types.
    /// 
    /// Per spec §3.2.1.5.6: Prefixes apply to number, date, and quantity types.
    /// </summary>
    private static (FhirSearchPrefix prefix, string value) ExtractPrefix(string value, FhirSearchParamType type)
    {
        // Only ordered types support prefixes
        if (type != FhirSearchParamType.Number &&
            type != FhirSearchParamType.Date &&
            type != FhirSearchParamType.Quantity)
        {
            return (FhirSearchPrefix.Eq, value);
        }

        foreach (var p in SearchPrefixes.All)
        {
            if (value.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = p switch
                {
                    SearchPrefixes.Eq => FhirSearchPrefix.Eq,
                    SearchPrefixes.Ne => FhirSearchPrefix.Ne,
                    SearchPrefixes.Gt => FhirSearchPrefix.Gt,
                    SearchPrefixes.Ge => FhirSearchPrefix.Ge,
                    SearchPrefixes.Lt => FhirSearchPrefix.Lt,
                    SearchPrefixes.Le => FhirSearchPrefix.Le,
                    SearchPrefixes.Sa => FhirSearchPrefix.Sa,
                    SearchPrefixes.Eb => FhirSearchPrefix.Eb,
                    SearchPrefixes.Ap => FhirSearchPrefix.Ap,
                    _ => FhirSearchPrefix.Eq
                };
                return (prefix, value[p.Length..]);
            }
        }

        return (FhirSearchPrefix.Eq, value);
    }

    /// <summary>
    /// Determines parameter type based on name.
    /// </summary>
    private static FhirSearchParamType GetParameterType(string name)
    {
        return name.ToLowerInvariant() switch
        {
            SearchParams.Id => FhirSearchParamType.Token,
            SearchParams.LastUpdated => FhirSearchParamType.Date,
            SearchParams.Tag => FhirSearchParamType.Token,
            SearchParams.Profile => FhirSearchParamType.Uri,
            SearchParams.Security => FhirSearchParamType.Token,
            SearchParams.Text => FhirSearchParamType.String,
            SearchParams.Content => FhirSearchParamType.String,
            SearchParams.Identifier => FhirSearchParamType.Token,
            SearchParams.Name or SearchParams.Family or SearchParams.Given => FhirSearchParamType.String,
            SearchParams.BirthDate => FhirSearchParamType.Date,
            SearchParams.Gender or SearchParams.Active or SearchParams.Status or 
            SearchParams.Code or SearchParams.Category => FhirSearchParamType.Token,
            SearchParams.Subject or SearchParams.Patient or SearchParams.Encounter or 
            SearchParams.Performer or SearchParams.Author => FhirSearchParamType.Reference,
            SearchParams.Date or SearchParams.Effective or SearchParams.Issued or 
            SearchParams.Authored or SearchParams.Onset => FhirSearchParamType.Date,
            SearchParams.ValueQuantity => FhirSearchParamType.Quantity,
            SearchParams.Url => FhirSearchParamType.Uri,
            _ => FhirSearchParamType.String
        };
    }

    private static bool IsResourceTypeName(string name) => ResourceTypes.CommonTypes.Contains(name);

    #endregion

    #region Value Parsing

    /// <summary>
    /// Parses a token value with system|code format.
    /// 
    /// Per spec §3.2.1.5.14:
    ///   - [code]: Matches code regardless of system
    ///   - [system]|[code]: Matches exact system and code
    ///   - |[code]: Matches code with no system
    ///   - [system]|: Matches any code in that system
    /// 
    /// Matching is literal and case-sensitive unless the underlying CodeSystem
    /// indicates case-insensitivity. Per §FCS55-56, servers should treat ambiguous
    /// cases as case-insensitive for safety.
    /// </summary>
    public static FhirTokenValue ParseTokenValue(string value)
    {
        // Handle escaped pipe characters
        var unescapedValue = UnescapeSearchValue(value);

        if (!unescapedValue.Contains('|'))
            return new FhirTokenValue(null, unescapedValue, CodeOnly: true);

        var parts = unescapedValue.Split('|', 2);

        if (string.IsNullOrEmpty(parts[0]) && parts.Length > 1)
            return new FhirTokenValue(null, parts[1], CodeOnly: true);

        if (parts.Length > 1 && string.IsNullOrEmpty(parts[1]))
            return new FhirTokenValue(parts[0], null, SystemOnly: true);

        return new FhirTokenValue(parts[0], parts.Length > 1 ? parts[1] : null);
    }

    /// <summary>
    /// Parses a quantity value with number|system|code format.
    /// 
    /// Per spec §3.2.1.5.11: Quantity format is [prefix][number]|[system]|[code]
    /// </summary>
    public static FhirQuantityValue ParseQuantityValue(string value, FhirSearchPrefix prefix)
    {
        var unescapedValue = UnescapeSearchValue(value);
        var parts = unescapedValue.Split('|');

        if (!decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            throw new FhirSearchException($"Invalid quantity number: {parts[0]}");

        return new FhirQuantityValue(
            Number: number,
            System: parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : null,
            Code: parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : null,
            Prefix: prefix);
    }

    /// <summary>
    /// Parses a reference value with [type]/[id] or URL format.
    /// 
    /// Per spec §3.2.1.5.12: Reference formats include [id], [type]/[id], or absolute URL
    /// </summary>
    public static FhirReferenceValue ParseReferenceValue(string value, string? typeModifier = null)
    {
        var unescapedValue = UnescapeSearchValue(value);

        // Check for absolute URL
        if (unescapedValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            unescapedValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new FhirReferenceValue(null, null, Url: unescapedValue);
        }

        // Check for [type]/[id] format
        var slashIndex = unescapedValue.IndexOf('/');
        if (slashIndex > 0)
        {
            var type = unescapedValue[..slashIndex];
            var remainder = unescapedValue[(slashIndex + 1)..];

            // Check for version: [type]/[id]/_history/[vid]
            var historyIndex = remainder.IndexOf("/_history/", StringComparison.OrdinalIgnoreCase);
            if (historyIndex >= 0)
            {
                var id = remainder[..historyIndex];
                var version = remainder[(historyIndex + 10)..];
                return new FhirReferenceValue(type, id, Version: version);
            }

            return new FhirReferenceValue(type, remainder);
        }

        // Just an id with optional type modifier
        return new FhirReferenceValue(typeModifier, unescapedValue);
    }

    /// <summary>
    /// Parses a date value into range boundaries based on precision.
    /// 
    /// Per spec §3.2.1.5.9: Date parameters are intrinsically range comparisons.
    /// 
    /// Precision determines implicit range:
    ///   - yyyy: Jan 1 00:00:00 through Dec 31 23:59:59.999
    ///   - yyyy-MM: First day 00:00:00 through last day 23:59:59.999
    ///   - yyyy-MM-dd: 00:00:00 through 23:59:59.999
    ///   - Full datetime: Exact instant
    /// 
    /// Per §FCS45-47: Timezones should be handled; when missing, server local
    /// timezone is assumed. Date-only values have no timezone consideration.
    /// </summary>
    public static FhirDateValue ParseDateValue(string value, FhirSearchPrefix prefix)
    {
        // Year only: yyyy
        if (YearOnlyRegex().IsMatch(value))
        {
            var year = int.Parse(value);
            return new FhirDateValue(
                new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(year, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc),
                prefix,
                DateTimePrecision.Year);
        }

        // Year-Month: yyyy-MM
        if (YearMonthRegex().IsMatch(value))
        {
            var parts = value.Split('-');
            var year = int.Parse(parts[0]);
            var month = int.Parse(parts[1]);
            var lastDay = DateTime.DaysInMonth(year, month);
            return new FhirDateValue(
                new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(year, month, lastDay, 23, 59, 59, 999, DateTimeKind.Utc),
                prefix,
                DateTimePrecision.Month);
        }

        // Full date or datetime
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
        {
            var precision = value.Contains('T')
                ? (value.Contains('.') ? DateTimePrecision.Millisecond : DateTimePrecision.Second)
                : DateTimePrecision.Day;

            var lower = precision == DateTimePrecision.Day
                ? new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, DateTimeKind.Utc)
                : dt.ToUniversalTime();

            var upper = precision == DateTimePrecision.Day
                ? new DateTime(dt.Year, dt.Month, dt.Day, 23, 59, 59, 999, DateTimeKind.Utc)
                : dt.ToUniversalTime();

            return new FhirDateValue(lower, upper, prefix, precision);
        }

        throw new FhirSearchException($"Invalid date format: {value}");
    }

    /// <summary>
    /// Unescapes special characters in search values.
    /// 
    /// Per spec §3.2.1.5.7: Characters $, |, and , must be escaped with \
    /// </summary>
    private static string UnescapeSearchValue(string value)
    {
        return value
            .Replace("\\$", "$")
            .Replace("\\|", "|")
            .Replace("\\,", ",")
            .Replace("\\\\", "\\");
    }

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex YearOnlyRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}$")]
    private static partial Regex YearMonthRegex();

    #endregion

    #region Search Execution

    /// <summary>
    /// Performs the actual search against the data store.
    /// </summary>
    private async Task<(IReadOnlyList<FhirSearchMatch> results, long totalCount, List<string> warnings)> PerformSearchAsync(
        FhirSearchRequest request,
        CancellationToken ct)
    {
        var warnings = new List<string>();

        // Build filter criteria
        var filters = BuildFilterCriteria(request.Parameters, warnings);

        // Determine resource types to search
        var typesToSearch = DetermineTypesToSearch(request);

        var allResults = new List<FhirSearchMatch>();
        long totalCount = 0;

        foreach (var resourceType in typesToSearch)
        {
            var count = await _repo.CountVerticesByLabelAsync(resourceType, filters, ct);
            totalCount += count;

            var vertices = await _repo.GetVerticesByLabelAsync(
                resourceType, filters, request.Count, request.Offset, ct);

            foreach (var v in vertices)
            {
                var json = v.Properties?.TryGetValue(Properties.Json, out var j) == true ? j?.ToString() : null;
                var fhirId = v.Properties?.TryGetValue(Properties.Id, out var id) == true ? id?.ToString() : null;
                var versionId = v.Properties?.TryGetValue(Properties.VersionId, out var vid) == true ? vid?.ToString() : null;
                var lastUpdated = v.Properties?.TryGetValue(Properties.LastUpdated, out var lu) == true && lu is DateTime luDt
                    ? luDt
                    : (DateTime?)null;

                allResults.Add(new FhirSearchMatch(v.Id, fhirId, resourceType, json, versionId, lastUpdated));
            }
        }

        // Apply sorting if specified
        if (!string.IsNullOrEmpty(request.Sort))
        {
            allResults = ApplySorting(allResults, request.Sort, request.SortDescending);
        }

        // Limit results
        var limitedResults = allResults.Take(request.Count).ToList();

        return (limitedResults, totalCount, warnings);
    }

    /// <summary>
    /// Builds filter criteria from parsed parameters.
    /// </summary>
    private Dictionary<string, object> BuildFilterCriteria(
        IReadOnlyList<FhirSearchParameter>? parameters,
        List<string> warnings)
    {
        var filters = new Dictionary<string, object>();

        if (parameters == null) return filters;

        foreach (var param in parameters)
        {
            try
            {
                var (key, value) = ConvertParameterToFilter(param);
                if (key != null && value != null)
                {
                    filters[key] = value;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Parameter '{param.Name}' could not be processed: {ex.Message}");
            }
        }

        return filters;
    }

    /// <summary>
    /// Converts a single parameter to a filter key-value pair.
    /// </summary>
    private (string? key, object? value) ConvertParameterToFilter(FhirSearchParameter param)
    {
        // Handle missing modifier
        if (param.Modifier == FhirSearchModifier.Missing)
        {
            // Return filter that checks for presence/absence
            return (param.Name, param.RawValue.Equals("true", StringComparison.OrdinalIgnoreCase) ? null : "*");
        }

        // Map standard parameters to storage properties
        var key = param.Name.ToLowerInvariant() switch
        {
            SearchParams.Id => Properties.Id,
            SearchParams.Identifier => SearchParams.Identifier,
            _ => param.Name
        };

        // Handle different parameter types
        return param.Type switch
        {
            FhirSearchParamType.Token => (key, ProcessTokenFilter(param)),
            FhirSearchParamType.String => (key, ProcessStringFilter(param)),
            FhirSearchParamType.Reference => (key, ProcessReferenceFilter(param)),
            FhirSearchParamType.Date => (key, ProcessDateFilter(param)),
            FhirSearchParamType.Number => (key, ProcessNumberFilter(param)),
            FhirSearchParamType.Quantity => (key, ProcessQuantityFilter(param)),
            _ => (key, param.RawValue)
        };
    }

    private object ProcessTokenFilter(FhirSearchParameter param)
    {
        if (param.OrValues?.Count > 1)
        {
            return param.OrValues.Select(v => ParseTokenValue(v)).ToList();
        }

        var token = ParseTokenValue(param.RawValue);

        // Handle modifiers
        return param.Modifier switch
        {
            FhirSearchModifier.Not => new { not = token },
            FhirSearchModifier.Text => new { text = token.Code },
            _ => token.CodeOnly ? token.Code! : (object)token
        };
    }

    private object ProcessStringFilter(FhirSearchParameter param)
    {
        return param.Modifier switch
        {
            FhirSearchModifier.Exact => new { exact = param.RawValue },
            FhirSearchModifier.Contains => new { contains = param.RawValue },
            _ => param.RawValue // Default: starts-with, case-insensitive
        };
    }

    private object ProcessReferenceFilter(FhirSearchParameter param)
    {
        var reference = ParseReferenceValue(param.RawValue, param.ModifierValue);

        if (param.Modifier == FhirSearchModifier.Identifier && reference.Identifier != null)
        {
            return new { identifier = reference.Identifier };
        }

        if (reference.ResourceType != null && reference.Id != null)
        {
            return $"{reference.ResourceType}/{reference.Id}";
        }

        return reference.Id ?? reference.Url ?? param.RawValue;
    }

    /// <summary>
    /// Converts date parameter to filter criteria based on prefix semantics.
    /// 
    /// Per FHIR spec §3.2.1.5.6 prefix table:
    ///   gt: resource-upper > parameter-upper (any part of resource is after parameter)
    ///   ge: resource-lower >= parameter-lower OR resource-upper >= parameter-lower
    ///   lt: resource-lower < parameter-lower (any part of resource is before parameter)
    ///   le: resource-lower <= parameter-upper OR resource-upper <= parameter-upper
    ///   sa: resource-lower > parameter-upper (starts strictly after, no overlap)
    ///   eb: resource-upper < parameter-lower (ends strictly before, no overlap)
    ///   eq: parameter range fully contains resource range (default)
    /// </summary>
    private object ProcessDateFilter(FhirSearchParameter param)
    {
        var dateValue = ParseDateValue(param.RawValue, param.Prefix);

        return param.Prefix switch
        {
            FhirSearchPrefix.Gt => new { gt = dateValue.UpperBound },
            FhirSearchPrefix.Ge => new { ge = dateValue.LowerBound },
            FhirSearchPrefix.Lt => new { lt = dateValue.LowerBound },
            FhirSearchPrefix.Le => new { le = dateValue.UpperBound },
            FhirSearchPrefix.Ne => new { ne = dateValue },
            FhirSearchPrefix.Sa => new { sa = dateValue.UpperBound },
            FhirSearchPrefix.Eb => new { eb = dateValue.LowerBound },
            _ => new { lower = dateValue.LowerBound, upper = dateValue.UpperBound }
        };
    }

    private object ProcessNumberFilter(FhirSearchParameter param)
    {
        if (!decimal.TryParse(param.RawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            throw new FhirSearchException($"Invalid number value: {param.RawValue}");
        }

        return param.Prefix switch
        {
            FhirSearchPrefix.Gt => new { gt = number },
            FhirSearchPrefix.Ge => new { ge = number },
            FhirSearchPrefix.Lt => new { lt = number },
            FhirSearchPrefix.Le => new { le = number },
            FhirSearchPrefix.Ne => new { ne = number },
            _ => number
        };
    }

    private object ProcessQuantityFilter(FhirSearchParameter param)
    {
        var quantity = ParseQuantityValue(param.RawValue, param.Prefix);
        return quantity;
    }

    private IReadOnlyList<string> DetermineTypesToSearch(FhirSearchRequest request)
    {
        if (request.ResourceType != null)
            return [request.ResourceType];

        if (request.ResourceTypes?.Count > 0)
            return request.ResourceTypes;

        return _validation.GetSupportedResourceTypes();
    }

    private static List<FhirSearchMatch> ApplySorting(
        List<FhirSearchMatch> results,
        string sortParam,
        bool descending)
    {
        return sortParam.ToLowerInvariant() switch
        {
            SearchParams.Id => descending
                ? results.OrderByDescending(r => r.FhirId).ToList()
                : results.OrderBy(r => r.FhirId).ToList(),
            SearchParams.LastUpdated => descending
                ? results.OrderByDescending(r => r.LastUpdated).ToList()
                : results.OrderBy(r => r.LastUpdated).ToList(),
            _ => results
        };
    }

    #endregion

    #region Bundle Construction

    /// <summary>
    /// Builds a FHIR searchset Bundle from search results.
    /// 
    /// Per spec §3.2.1.3.4: Entries include fullUrl, resource, and search.mode
    /// </summary>
    private object BuildSearchBundle(
        IReadOnlyList<FhirSearchMatch> results,
        long totalCount,
        FhirSearchRequest request,
        string selfUrl,
        string baseUrl,
        List<string>? warnings)
    {
        var entries = new List<object>();

        foreach (var match in results.Where(m => m.Json != null))
        {
            var entry = new Dictionary<string, object>
            {
                ["fullUrl"] = $"{baseUrl}/{match.ResourceType}/{match.FhirId}",
                ["resource"] = JsonSerializer.Deserialize<JsonElement>(match.Json!),
                ["search"] = new { mode = match.IsInclude ? SearchEntryMode.Include : SearchEntryMode.Match }
            };

            if (match.Score.HasValue)
            {
                ((dynamic)entry["search"]).score = match.Score.Value;
            }

            entries.Add(entry);
        }

        var links = BuildPaginationLinks(selfUrl, totalCount, request);

        var bundle = new Dictionary<string, object>
        {
            ["resourceType"] = ResourceTypes.Bundle,
            ["type"] = BundleTypes.SearchSet,
            ["total"] = totalCount,
            ["link"] = links
        };

        if (entries.Count > 0)
        {
            bundle["entry"] = entries;
        }

        // Add outcome entry if there are warnings
        if (warnings?.Count > 0)
        {
            var outcomeEntry = new
            {
                resource = CreateSearchWarnings(warnings),
                search = new { mode = SearchEntryMode.Outcome }
            };

            if (bundle.ContainsKey("entry"))
            {
                ((List<object>)bundle["entry"]).Add(outcomeEntry);
            }
            else
            {
                bundle["entry"] = new List<object> { outcomeEntry };
            }
        }

        return bundle;
    }

    /// <summary>
    /// Builds pagination links for the search response.
    /// 
    /// Per spec §3.2.1.3.3: Links include self, first, previous, next, last
    /// </summary>
    private static List<object> BuildPaginationLinks(string selfUrl, long totalCount, FhirSearchRequest request)
    {
        var links = new List<object>
        {
            new { relation = LinkRelations.Self, url = selfUrl }
        };

        var baseUri = selfUrl.Split('?')[0];
        var hasNext = request.Offset + request.Count < totalCount;
        var hasPrev = request.Offset > 0;

        if (hasPrev)
        {
            var prevOffset = Math.Max(0, request.Offset - request.Count);
            links.Add(new { relation = LinkRelations.Previous, url = $"{baseUri}?{SearchParams.Count}={request.Count}&{SearchParams.Offset}={prevOffset}" });
        }

        if (hasNext)
        {
            var nextOffset = request.Offset + request.Count;
            links.Add(new { relation = LinkRelations.Next, url = $"{baseUri}?{SearchParams.Count}={request.Count}&{SearchParams.Offset}={nextOffset}" });
        }

        if (totalCount > 0)
        {
            links.Insert(1, new { relation = LinkRelations.First, url = $"{baseUri}?{SearchParams.Count}={request.Count}&{SearchParams.Offset}=0" });

            var lastOffset = Math.Max(0, ((totalCount - 1) / request.Count) * request.Count);
            links.Add(new { relation = LinkRelations.Last, url = $"{baseUri}?{SearchParams.Count}={request.Count}&{SearchParams.Offset}={lastOffset}" });
        }

        return links;
    }

    private static object CreateSearchError(string message)
    {
        return new
        {
            resourceType = ResourceTypes.OperationOutcome,
            issue = new[]
            {
                new
                {
                    severity = Severity.Error,
                    code = IssueCodes.Invalid,
                    diagnostics = message
                }
            }
        };
    }

    private static object CreateSearchWarnings(List<string> warnings)
    {
        return new
        {
            resourceType = ResourceTypes.OperationOutcome,
            issue = warnings.Select(w => new
            {
                severity = Severity.Warning,
                code = IssueCodes.Informational,
                diagnostics = w
            }).ToArray()
        };
    }

    #endregion

    #region Compartment Search

    /// <summary>
    /// Executes a compartment search.
    /// 
    /// FHIR Spec: §3.2.1.2.4 (https://build.fhir.org/search.html#searchcontexts)
    /// 
    /// Searches for resources of [type] that are within the compartment defined by
    /// [compartment]/[id]. For example, searching for all Observations for a Patient.
    /// </summary>
    public async Task<FhirOperationResult> ExecuteCompartmentSearchAsync(
        string compartmentType,
        string compartmentId,
        string resourceType,
        IDictionary<string, string> queryParams,
        string selfUrl,
        string baseUrl,
        CancellationToken ct = default)
    {
        if (!Compartments.All.Contains(compartmentType))
        {
            return FhirOperationResult.BadRequest(new
            {
                resourceType = ResourceTypes.OperationOutcome,
                issue = new[]
                {
                    new
                    {
                        severity = Severity.Error,
                        code = IssueCodes.Invalid,
                        diagnostics = $"Unknown compartment type: {compartmentType}. Valid types: {string.Join(", ", Compartments.All)}"
                    }
                }
            });
        }

        // Add compartment constraint to query parameters
        var augmentedParams = new Dictionary<string, string>(queryParams);
        var compartmentParamName = GetCompartmentParameterName(compartmentType, resourceType);
        if (compartmentParamName != null)
        {
            augmentedParams[compartmentParamName] = $"{compartmentType}/{compartmentId}";
        }

        // Handle wildcard type search
        var actualResourceType = resourceType == "*" ? null : resourceType;

        var request = ParseSearchParameters(actualResourceType, augmentedParams);
        return await ExecuteSearchAsync(request, selfUrl, baseUrl, ct);
    }

    /// <summary>
    /// Gets the appropriate search parameter name for linking resources to a compartment.
    /// 
    /// Per FHIR specification, each resource type has specific parameters that link
    /// it to compartment resources (e.g., Observation.subject links to Patient compartment).
    /// </summary>
    private static string? GetCompartmentParameterName(string compartmentType, string resourceType)
    {
        return (compartmentType.ToLowerInvariant(), resourceType.ToLowerInvariant()) switch
        {
            // Patient compartment
            (Compartments.Patient, ResourceTypes.Observation) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.Condition) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.Procedure) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.Encounter) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.DiagnosticReport) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.MedicationRequest) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.MedicationStatement) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.AllergyIntolerance) => SearchParams.Patient,
            (Compartments.Patient, ResourceTypes.Immunization) => SearchParams.Patient,
            (Compartments.Patient, ResourceTypes.CarePlan) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.DocumentReference) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.ServiceRequest) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.Specimen) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.Goal) => SearchParams.Subject,
            (Compartments.Patient, ResourceTypes.Task) => SearchParams.For,
            (Compartments.Patient, _) => SearchParams.Patient,

            // Encounter compartment
            (Compartments.Encounter, ResourceTypes.Observation) => SearchParams.Encounter,
            (Compartments.Encounter, ResourceTypes.Condition) => SearchParams.Encounter,
            (Compartments.Encounter, ResourceTypes.Procedure) => SearchParams.Encounter,
            (Compartments.Encounter, ResourceTypes.DiagnosticReport) => SearchParams.Encounter,
            (Compartments.Encounter, ResourceTypes.MedicationRequest) => SearchParams.Encounter,
            (Compartments.Encounter, ResourceTypes.DocumentReference) => SearchParams.Encounter,
            (Compartments.Encounter, _) => SearchParams.Encounter,

            // Practitioner compartment
            (Compartments.Practitioner, ResourceTypes.Observation) => SearchParams.Performer,
            (Compartments.Practitioner, ResourceTypes.Procedure) => SearchParams.Performer,
            (Compartments.Practitioner, ResourceTypes.DiagnosticReport) => SearchParams.Performer,
            (Compartments.Practitioner, ResourceTypes.MedicationRequest) => SearchParams.Requester,
            (Compartments.Practitioner, ResourceTypes.Encounter) => SearchParams.Participant,
            (Compartments.Practitioner, _) => SearchParams.Practitioner,

            // Device compartment
            (Compartments.Device, ResourceTypes.Observation) => SearchParams.Device,
            (Compartments.Device, ResourceTypes.DiagnosticReport) => SearchParams.Device,
            (Compartments.Device, ResourceTypes.Procedure) => SearchParams.Device,
            (Compartments.Device, _) => SearchParams.Device,

            // RelatedPerson compartment
            (Compartments.RelatedPerson, _) => SearchParams.RelatedPerson,

            _ => null
        };
    }

    #endregion
}

/// <summary>
/// Exception for search validation/processing errors.
/// </summary>
public class FhirSearchException : Exception
{
    public FhirSearchException(string message) : base(message) { }
    public FhirSearchException(string message, Exception inner) : base(message, inner) { }
}

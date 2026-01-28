namespace BLL.Models;

/// <summary>
/// FHIR search parameter types defining how parameter values are interpreted and matched.
/// 
/// Reference: FHIR R6 §3.2.1.5 (https://build.fhir.org/search.html#ptypes)
/// 
/// Types determine:
/// - How values are parsed (e.g., Number parses decimals, Date parses ISO 8601)
/// - Which prefixes are allowed (Number/Date/Quantity support comparators)
/// - Which modifiers are valid (String supports :exact/:contains, Token supports :in/:not)
/// - How matching logic works (String is case-insensitive starts-with, Token is literal)
/// </summary>
public enum FhirSearchParamType
{
    Number,
    Date,
    String,
    Token,
    Reference,
    Composite,
    Quantity,
    Uri,
    Special,
    Resource
}

/// <summary>
/// FHIR search prefixes (comparators) for ordered parameter types (Number, Date, Quantity).
/// 
/// Reference: FHIR R6 §3.2.1.5.6 (https://build.fhir.org/search.html#prefix)
/// 
/// Prefixes control comparison behavior against ranges:
/// - Eq (default): Resource range fully contained by parameter range
/// - Ne: Resource range not fully contained by parameter range  
/// - Gt/Lt: Upper/lower boundary comparison
/// - Ge/Le: Inclusive boundary comparison
/// - Sa/Eb: Strict non-overlapping range comparison (starts-after/ends-before)
/// - Ap: Approximate match (typically 10% tolerance)
/// 
/// When no prefix is specified, 'eq' is assumed. Precision matters: the value "100"
/// implies a range of [99.5, 100.5), while "100.00" implies [99.995, 100.005).
/// </summary>
public enum FhirSearchPrefix
{
    Eq,
    Ne,
    Gt,
    Ge,
    Lt,
    Le,
    Sa,
    Eb,
    Ap
}

/// <summary>
/// FHIR search modifiers that alter how search parameters are processed.
/// 
/// Reference: FHIR R6 §3.2.1.5.5 (https://build.fhir.org/search.html#modifiers)
/// 
/// Modifiers change parameter behavior without changing the target element:
/// - None: Default behavior for the parameter type
/// - Missing: Tests for presence (true) or absence (false) of values
/// - Exact/Contains: String matching modes (literal vs substring)
/// - Above/Below: Hierarchical matching for tokens, references, and URIs
/// - In/NotIn: ValueSet membership testing for tokens
/// - Not: Negates token matching (includes resources without the element)
/// - Text/TextAdvanced: Human-readable text searching on coded elements
/// - CodeText: Searches the code value as a string
/// - Identifier: Matches Reference.identifier instead of Reference.reference
/// - OfType: Matches Identifier by type|system|value format
/// - Type: Restricts reference target to specific resource type (e.g., subject:Patient)
/// - Iterate: Applies _include/_revinclude to included resources recursively
/// 
/// Servers must reject unknown/unsupported modifiers with HTTP 400.
/// </summary>
public enum FhirSearchModifier
{
    None,
    Above,
    Below,
    CodeText,
    Contains,
    Exact,
    Identifier,
    In,
    Iterate,
    Missing,
    Not,
    NotIn,
    OfType,
    Text,
    TextAdvanced,
    Type
}

/// <summary>
/// Parsed representation of a single FHIR search parameter from a query string.
/// 
/// Input format: [name]:[modifier]=[prefix][value]
/// Examples:
///   - birthdate=ge2000-01-01 (name="birthdate", prefix=Ge, value="2000-01-01")
///   - name:exact=Peter (name="name", modifier=Exact, value="Peter")
///   - subject:Patient=123 (name="subject", modifier=Type, modifierValue="Patient", value="123")
///   - code=http://loinc.org|1234-5 (token with system|code)
///   - gender=male,female (OR-joined values)
/// </summary>
/// <param name="Name">The parameter name (e.g., "birthdate", "identifier").</param>
/// <param name="Type">The search parameter type determining parsing and matching behavior.</param>
/// <param name="Modifier">Optional modifier altering search behavior (e.g., :exact, :missing).</param>
/// <param name="ModifierValue">Value for Type modifier specifying resource type (e.g., "Patient").</param>
/// <param name="Prefix">Comparison prefix for ordered types (default: Eq).</param>
/// <param name="RawValue">The unprocessed parameter value after prefix extraction.</param>
/// <param name="OrValues">Comma-separated values parsed into list for OR matching.</param>
/// <param name="ChainedPath">For chained parameters, the dot-separated path (e.g., "patient.name").</param>
public sealed record FhirSearchParameter(
    string Name,
    FhirSearchParamType Type,
    FhirSearchModifier Modifier = FhirSearchModifier.None,
    string? ModifierValue = null,
    FhirSearchPrefix Prefix = FhirSearchPrefix.Eq,
    string RawValue = "",
    IReadOnlyList<string>? OrValues = null,
    string? ChainedPath = null);

/// <summary>
/// Parsed token search value supporting system|code format.
/// 
/// Reference: FHIR R6 §3.2.1.5.14 (https://build.fhir.org/search.html#token)
/// 
/// Token format variations:
///   - [code] alone: Matches any system with that code
///   - |[code]: Matches code with no system defined  
///   - [system]|[code]: Matches exact system and code
///   - [system]|: Matches any code in that system
/// 
/// Used for Coding, CodeableConcept, Identifier, ContactPoint, code, boolean, id.
/// </summary>
/// <param name="System">The coding system URI (null if not specified).</param>
/// <param name="Code">The code value (null if system-only search).</param>
/// <param name="SystemOnly">True if searching for any code within a system.</param>
/// <param name="CodeOnly">True if searching for a code regardless of system.</param>
public sealed record FhirTokenValue(
    string? System,
    string? Code,
    bool SystemOnly = false,
    bool CodeOnly = false);

/// <summary>
/// Parsed quantity search value supporting number|system|code format.
/// 
/// Reference: FHIR R6 §3.2.1.5.11 (https://build.fhir.org/search.html#quantity)
/// 
/// Quantity format: [prefix][number]|[system]|[code]
/// Examples:
///   - 5.4 (value only, any unit)
///   - 5.4||mg (value and unit code, any system)
///   - 5.4|http://unitsofmeasure.org|mg (full UCUM unit)
///   - le5.4|http://unitsofmeasure.org|mg (with prefix)
/// 
/// Servers may perform canonical unit conversion (e.g., treating mg and g as comparable).
/// </summary>
/// <param name="Number">The numeric value with implicit precision range.</param>
/// <param name="System">The unit system URI (typically UCUM).</param>
/// <param name="Code">The unit code within the system.</param>
/// <param name="Prefix">Comparison operator for range matching.</param>
public sealed record FhirQuantityValue(
    decimal Number,
    string? System = null,
    string? Code = null,
    FhirSearchPrefix Prefix = FhirSearchPrefix.Eq);

/// <summary>
/// Parsed reference search value supporting multiple formats.
/// 
/// Reference: FHIR R6 §3.2.1.5.12 (https://build.fhir.org/search.html#reference)
/// 
/// Reference format variations:
///   - [id]: Logical ID with type inferred from context
///   - [type]/[id]: Type-qualified relative reference
///   - [type]/[id]/_history/[vid]: Versioned reference
///   - [url]: Absolute URL reference
///   - :identifier modifier uses Identifier format instead
/// 
/// When the :identifier modifier is used, Reference.identifier is searched
/// instead of Reference.reference.
/// </summary>
/// <param name="ResourceType">The target resource type (from value or :Type modifier).</param>
/// <param name="Id">The logical resource ID.</param>
/// <param name="Version">Optional specific version ID.</param>
/// <param name="Url">Absolute URL for external references.</param>
/// <param name="Identifier">Token value when using :identifier modifier.</param>
public sealed record FhirReferenceValue(
    string? ResourceType,
    string? Id,
    string? Version = null,
    string? Url = null,
    FhirTokenValue? Identifier = null);

/// <summary>
/// Parsed date search value with computed range boundaries.
/// 
/// Reference: FHIR R6 §3.2.1.5.9 (https://build.fhir.org/search.html#date)
/// 
/// Date format: yyyy-mm-ddThh:mm:ss.ssss[Z|(+|-)hh:mm]
/// 
/// Dates are inherently ranges based on precision:
///   - 2015: [2015-01-01T00:00:00, 2015-12-31T23:59:59.999]
///   - 2015-08: [2015-08-01T00:00:00, 2015-08-31T23:59:59.999]
///   - 2015-08-12: [2015-08-12T00:00:00, 2015-08-12T23:59:59.999]
/// 
/// The prefix determines how the parameter range compares to resource values.
/// </summary>
/// <param name="LowerBound">Earliest instant in the implied range (inclusive).</param>
/// <param name="UpperBound">Latest instant in the implied range (inclusive).</param>
/// <param name="Prefix">Comparison operator determining match semantics.</param>
/// <param name="Precision">The precision level determining range width.</param>
public sealed record FhirDateValue(
    DateTime LowerBound,
    DateTime UpperBound,
    FhirSearchPrefix Prefix = FhirSearchPrefix.Eq,
    DateTimePrecision Precision = DateTimePrecision.Day);

/// <summary>
/// Precision levels for FHIR date/time search values, determining implicit range boundaries.
/// 
/// Reference: FHIR R6 §3.2.1.5.9 (https://build.fhir.org/search.html#date)
/// 
/// Date searches are range comparisons based on precision:
/// - Year (2015): Jan 1 00:00:00 through Dec 31 23:59:59.999
/// - Month (2015-08): Aug 1 00:00:00 through Aug 31 23:59:59.999
/// - Day (2015-08-12): 00:00:00 through 23:59:59.999
/// - Time precision uses the exact instant specified
/// </summary>
public enum DateTimePrecision
{
    Year,
    Month,
    Day,
    Hour,
    Minute,
    Second,
    Millisecond
}

/// <summary>
/// Complete FHIR search request with all filtering and result modification options.
/// 
/// Search Contexts (§3.2.1.2.4):
///   - System: No ResourceType, uses _type to filter (GET [base]?...)
///   - Type: Single ResourceType specified (GET [base]/[type]?...)
///   - Compartment: Patient/123/Observation?... (not yet implemented)
/// 
/// Processing Order:
///   1. Filter parameters applied (AND-joined)
///   2. Results sorted by Sort parameter
///   3. Pagination applied (Offset, Count)
///   4. Included resources added (Include, RevInclude)
/// 
/// Per spec: Multiple values for same parameter are AND-joined;
/// comma-separated values within a parameter are OR-joined.
/// </summary>
/// <param name="ResourceType">Single resource type for type-level search.</param>
/// <param name="ResourceTypes">Types from _type parameter for system search.</param>
/// <param name="Parameters">Parsed filter parameters (AND-joined).</param>
/// <param name="Count">Maximum entries per page (_count, default 100, max 1000).</param>
/// <param name="Offset">Starting position in result set (_offset).</param>
/// <param name="Sort">Sort parameter name (_sort, prefix '-' for descending).</param>
/// <param name="SortDescending">True if sort order is descending.</param>
/// <param name="Summary">Summary mode (_summary: true/text/data/count/false).</param>
/// <param name="Elements">Specific elements to include (_elements).</param>
/// <param name="Include">Forward references to include (_include).</param>
/// <param name="RevInclude">Reverse references to include (_revinclude).</param>
/// <param name="Total">Total count mode (_total: none/estimate/accurate).</param>
/// <param name="ContainedSearch">Whether to search contained resources (_contained).</param>
public sealed record FhirSearchRequest(
    string? ResourceType = null,
    IReadOnlyList<string>? ResourceTypes = null,
    IReadOnlyList<FhirSearchParameter>? Parameters = null,
    int Count = 100,
    int Offset = 0,
    string? Sort = null,
    bool SortDescending = false,
    string? Summary = null,
    IReadOnlyList<string>? Elements = null,
    IReadOnlyList<string>? Include = null,
    IReadOnlyList<string>? RevInclude = null,
    long? Total = null,
    bool ContainedSearch = false);

/// <summary>
/// Single resource match from search execution, ready for Bundle entry construction.
/// 
/// Reference: FHIR R6 §3.2.1.3.4 (https://build.fhir.org/search.html#entries)
/// 
/// Search mode indicates why the entry is in the result:
///   - match: Resource meets the search criteria
///   - include: Resource included via _include/_revinclude
///   - outcome: OperationOutcome with search processing information
/// 
/// When a resource qualifies as both match and include, mode is 'match'.
/// </summary>
/// <param name="GraphId">Internal graph database identifier.</param>
/// <param name="FhirId">The resource's logical ID (Resource.id).</param>
/// <param name="ResourceType">The resource type name.</param>
/// <param name="Json">Serialized resource content for Bundle.entry.resource.</param>
/// <param name="VersionId">Resource version (meta.versionId) for ETag.</param>
/// <param name="LastUpdated">Last modification time (meta.lastUpdated).</param>
/// <param name="Score">Relevance score for ranked searches (_score).</param>
/// <param name="IsInclude">True if included via _include/_revinclude (mode="include").</param>
public sealed record FhirSearchMatch(
    string GraphId,
    string? FhirId,
    string ResourceType,
    string? Json,
    string? VersionId = null,
    DateTime? LastUpdated = null,
    decimal? Score = null,
    bool IsInclude = false);

/// <summary>
/// Complete search result set with pagination metadata for Bundle construction.
/// 
/// Reference: FHIR R6 §3.2.1.3 (https://build.fhir.org/search.html#return)
/// 
/// The self link (§3.2.1.3.2) must be a GET URL with the parameters actually used,
/// allowing clients to verify search behavior. Servers may omit sensitive parameters.
/// 
/// Pagination links (§3.2.1.3.3) are expressed as GET requests. Common relations:
///   - self: Current page parameters
///   - first: First page of results
///   - previous/prev: Prior page (synonyms)
///   - next: Following page
///   - last: Final page
/// </summary>
/// <param name="Matches">Resources in this page of results.</param>
/// <param name="TotalCount">Total matching resources across all pages.</param>
/// <param name="Offset">Current position in full result set.</param>
/// <param name="Count">Page size used for this request.</param>
/// <param name="SelfUrl">URL representing the executed search.</param>
/// <param name="FirstUrl">URL for first page (null if on first page).</param>
/// <param name="PreviousUrl">URL for previous page (null if on first page).</param>
/// <param name="NextUrl">URL for next page (null if on last page).</param>
/// <param name="LastUrl">URL for last page (null if on last page).</param>
/// <param name="Warnings">Processing warnings to include as outcome entry.</param>
public sealed record FhirSearchResultSet(
    IReadOnlyList<FhirSearchMatch> Matches,
    long TotalCount,
    int Offset,
    int Count,
    string SelfUrl,
    string? FirstUrl = null,
    string? PreviousUrl = null,
    string? NextUrl = null,
    string? LastUrl = null,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Search parameter definition for CapabilityStatement.rest.resource.searchParam.
/// 
/// Reference: FHIR R6 CapabilityStatement (https://build.fhir.org/capabilitystatement.html)
/// 
/// Defines a search parameter's capabilities including supported modifiers,
/// target types (for references), and combination support.
/// </summary>
/// <param name="Name">Parameter name as used in search URLs.</param>
/// <param name="Type">Search parameter type.</param>
/// <param name="Expression">FHIRPath expression identifying target elements.</param>
/// <param name="Documentation">Human-readable description of the parameter.</param>
/// <param name="Modifiers">Supported modifiers (null = type defaults).</param>
/// <param name="Targets">For references, allowed target resource types.</param>
/// <param name="Comparators">Supported prefixes for ordered types.</param>
/// <param name="MultipleOr">Whether comma-separated OR values are supported.</param>
/// <param name="MultipleAnd">Whether repeated parameter AND is supported.</param>
public sealed record FhirSearchParamDefinition(
    string Name,
    FhirSearchParamType Type,
    string Expression,
    string? Documentation = null,
    IReadOnlyList<FhirSearchModifier>? Modifiers = null,
    IReadOnlyList<string>? Targets = null,
    IReadOnlyList<string>? Comparators = null,
    bool MultipleOr = true,
    bool MultipleAnd = true);

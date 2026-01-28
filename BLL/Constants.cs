namespace BLL;

/// <summary>
/// Centralized constants for the BLL layer to eliminate magic strings.
/// </summary>
public static class Constants
{
    /// <summary>
    /// FHIR version identifiers.
    /// </summary>
    public static class FhirVersions
    {
        public const string R6Ballot3 = "6.0.0-ballot3";
        public const string R5 = "5.0.0";
    }

    /// <summary>
    /// FHIR resource property names stored in graph vertices.
    /// </summary>
    public static class Properties
    {
        public const string ResourceType = "resourceType";
        public const string Json = "json";
        public const string Id = "id";
        public const string IsPlaceholder = "isPlaceholder";
        public const string IsDeleted = "isDeleted";
        public const string IsCurrent = "isCurrent";
        public const string VersionId = "versionId";
        public const string LastUpdated = "lastUpdated";
        public const string Reference = "reference";
    }

    /// <summary>
    /// FHIR edge property names.
    /// </summary>
    public static class EdgeProperties
    {
        public const string Path = "path";
        public const string TargetResourceType = "targetResourceType";
        public const string TargetFhirId = "targetFhirId";
    }

    /// <summary>
    /// Graph edge directions.
    /// </summary>
    public static class EdgeDirection
    {
        public const string Out = "out";
        public const string In = "in";
    }

    /// <summary>
    /// Edge label prefixes and patterns.
    /// </summary>
    public static class EdgeLabels
    {
        public const string FhirReferencePrefix = "fhir:ref:";
    }

    /// <summary>
    /// FHIR resource type names.
    /// </summary>
    public static class ResourceTypes
    {
        public const string Bundle = "Bundle";
        public const string Patient = "Patient";
        public const string Practitioner = "Practitioner";
        public const string Organization = "Organization";
        public const string Location = "Location";
        public const string Encounter = "Encounter";
        public const string Observation = "Observation";
        public const string Condition = "Condition";
        public const string Procedure = "Procedure";
        public const string MedicationRequest = "MedicationRequest";
        public const string MedicationStatement = "MedicationStatement";
        public const string DiagnosticReport = "DiagnosticReport";
        public const string CarePlan = "CarePlan";
        public const string AllergyIntolerance = "AllergyIntolerance";
        public const string Immunization = "Immunization";
        public const string DocumentReference = "DocumentReference";
        public const string ServiceRequest = "ServiceRequest";
        public const string Device = "Device";
        public const string Specimen = "Specimen";
        public const string Group = "Group";
        public const string RelatedPerson = "RelatedPerson";
        public const string Goal = "Goal";
        public const string Task = "Task";
        public const string CapabilityStatement = "CapabilityStatement";
        public const string OperationOutcome = "OperationOutcome";

        /// <summary>
        /// Common resource types for reference target validation.
        /// </summary>
        public static readonly HashSet<string> CommonTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            Patient, Practitioner, Organization, Location, Encounter,
            Observation, Condition, Procedure, MedicationRequest, DiagnosticReport,
            CarePlan, AllergyIntolerance, Immunization, DocumentReference,
            ServiceRequest, Device, Specimen, Group, RelatedPerson
        };
    }

    /// <summary>
    /// FHIR Bundle type values.
    /// </summary>
    public static class BundleTypes
    {
        public const string Transaction = "transaction";
        public const string Batch = "batch";
        public const string TransactionResponse = "transaction-response";
        public const string BatchResponse = "batch-response";
        public const string SearchSet = "searchset";
        public const string History = "history";
    }

    /// <summary>
    /// FHIR Bundle entry property names.
    /// </summary>
    public static class BundleEntry
    {
        public const string Entry = "entry";
        public const string Request = "request";
        public const string Resource = "resource";
        public const string FullUrl = "fullUrl";
        public const string Method = "method";
        public const string Url = "url";
        public const string Type = "type";
    }

    /// <summary>
    /// HTTP method names.
    /// </summary>
    public static class HttpMethods
    {
        public const string Get = "GET";
        public const string Post = "POST";
        public const string Put = "PUT";
        public const string Delete = "DELETE";
        public const string Patch = "PATCH";
    }

    /// <summary>
    /// FHIR content types.
    /// </summary>
    public static class ContentTypes
    {
        public const string FhirJson = "application/fhir+json";
        public const string Json = "application/json";
        public const string JsonPatch = "application/json-patch+json";
    }

    /// <summary>
    /// FHIR OperationOutcome severity levels.
    /// </summary>
    public static class Severity
    {
        public const string Fatal = "fatal";
        public const string Error = "error";
        public const string Warning = "warning";
        public const string Information = "information";
    }

    /// <summary>
    /// FHIR OperationOutcome issue codes.
    /// </summary>
    public static class IssueCodes
    {
        public const string Invalid = "invalid";
        public const string NotFound = "not-found";
        public const string Deleted = "deleted";
        public const string Duplicate = "duplicate";
        public const string Conflict = "conflict";
        public const string MultipleMatches = "multiple-matches";
        public const string Exception = "exception";
        public const string Informational = "informational";
    }

    /// <summary>
    /// FHIR schema property names.
    /// </summary>
    public static class SchemaProperties
    {
        public const string Discriminator = "discriminator";
        public const string Mapping = "mapping";
    }

    /// <summary>
    /// Boolean string values.
    /// </summary>
    public static class BooleanStrings
    {
        public const string True = "true";
        public const string False = "false";
    }

    /// <summary>
    /// HTTP status codes as strings (for bundle responses).
    /// </summary>
    public static class StatusCodes
    {
        public const string Ok = "200";
        public const string Created = "201";
        public const string NoContent = "204";
        public const string BadRequest = "400";
        public const string NotFound = "404";
        public const string MethodNotAllowed = "405";
        public const string UnprocessableEntity = "422";
        public const string InternalServerError = "500";
        public const string NotImplemented = "501";
    }

    /// <summary>
    /// FHIR interaction codes for CapabilityStatement.
    /// </summary>
    public static class InteractionCodes
    {
        public const string Read = "read";
        public const string VRead = "vread";
        public const string Update = "update";
        public const string Patch = "patch";
        public const string Delete = "delete";
        public const string HistoryInstance = "history-instance";
        public const string HistoryType = "history-type";
        public const string Create = "create";
        public const string SearchType = "search-type";
        public const string Transaction = "transaction";
        public const string Batch = "batch";
        public const string SearchSystem = "search-system";
        public const string HistorySystem = "history-system";
    }

    /// <summary>
    /// FHIR search parameter names (standard parameters that apply to all resources).
    /// </summary>
    public static class SearchParams
    {
        public const string Id = "_id";
        public const string LastUpdated = "_lastUpdated";
        public const string Tag = "_tag";
        public const string Profile = "_profile";
        public const string Security = "_security";
        public const string Text = "_text";
        public const string Content = "_content";
        public const string List = "_list";
        public const string Has = "_has";
        public const string Type = "_type";
        public const string Query = "_query";
        public const string Filter = "_filter";
        public const string Sort = "_sort";
        public const string Count = "_count";
        public const string Offset = "_offset";
        public const string Include = "_include";
        public const string RevInclude = "_revinclude";
        public const string Summary = "_summary";
        public const string Elements = "_elements";
        public const string Contained = "_contained";
        public const string ContainedType = "_containedType";
        public const string Total = "_total";
        public const string Score = "_score";

        // Common resource search parameters
        public const string Identifier = "identifier";
        public const string Name = "name";
        public const string Family = "family";
        public const string Given = "given";
        public const string BirthDate = "birthdate";
        public const string Gender = "gender";
        public const string Active = "active";
        public const string Status = "status";
        public const string Code = "code";
        public const string Category = "category";
        public const string Subject = "subject";
        public const string Patient = "patient";
        public const string Encounter = "encounter";
        public const string Performer = "performer";
        public const string Author = "author";
        public const string Date = "date";
        public const string Effective = "effective";
        public const string Issued = "issued";
        public const string Authored = "authored";
        public const string Onset = "onset";
        public const string ValueQuantity = "value-quantity";
        public const string Url = "url";
        public const string Requester = "requester";
        public const string Participant = "participant";
        public const string Device = "device";
        public const string RelatedPerson = "relatedperson";
        public const string Practitioner = "practitioner";
        public const string For = "for";
    }

    /// <summary>
    /// FHIR search prefix (comparator) strings for ordered types.
    /// </summary>
    public static class SearchPrefixes
    {
        public const string Eq = "eq";
        public const string Ne = "ne";
        public const string Gt = "gt";
        public const string Ge = "ge";
        public const string Lt = "lt";
        public const string Le = "le";
        public const string Sa = "sa";
        public const string Eb = "eb";
        public const string Ap = "ap";

        public static readonly string[] All = [Eq, Ne, Gt, Ge, Lt, Le, Sa, Eb, Ap];
    }

    /// <summary>
    /// FHIR search modifier strings as used in query parameters.
    /// </summary>
    public static class SearchModifiers
    {
        public const string Above = "above";
        public const string Below = "below";
        public const string CodeText = "code-text";
        public const string Contains = "contains";
        public const string Exact = "exact";
        public const string Identifier = "identifier";
        public const string In = "in";
        public const string Iterate = "iterate";
        public const string Missing = "missing";
        public const string Not = "not";
        public const string NotIn = "not-in";
        public const string OfType = "of-type";
        public const string Text = "text";
        public const string TextAdvanced = "text-advanced";
    }

    /// <summary>
    /// FHIR Bundle link relation types for pagination.
    /// </summary>
    public static class LinkRelations
    {
        public const string Self = "self";
        public const string First = "first";
        public const string Previous = "previous";
        public const string Next = "next";
        public const string Last = "last";
    }

    /// <summary>
    /// FHIR search entry mode values for Bundle.entry.search.mode.
    /// </summary>
    public static class SearchEntryMode
    {
        public const string Match = "match";
        public const string Include = "include";
        public const string Outcome = "outcome";
    }

    /// <summary>
    /// FHIR compartment type names.
    /// </summary>
    public static class Compartments
    {
        public const string Patient = "Patient";
        public const string Encounter = "Encounter";
        public const string Practitioner = "Practitioner";
        public const string Device = "Device";
        public const string RelatedPerson = "RelatedPerson";

        public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
        {
            Patient, Encounter, Practitioner, Device, RelatedPerson
        };
    }

    /// <summary>
    /// Common status values for API responses.
    /// </summary>
    public static class Status
    {
        public const string Ok = "ok";
        public const string Wiped = "wiped";
        public const string Active = "active";
        public const string Server = "server";
        public const string Instance = "instance";
        public const string Versioned = "versioned";
        public const string Single = "single";
        public const string Self = "self";
        public const string Match = "match";
        public const string Token = "token";
    }
}

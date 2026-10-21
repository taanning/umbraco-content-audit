using System.Text.Json;
using System.Text.Json.Serialization;
using PropertyAuditTool.Models;
using PropertyAuditTool.Persistence;
using Umbraco.Cms.Core.Models.Membership;

namespace PropertyAuditTool.Services;

/// <summary>
/// Saved-query semantics: personal queries are owner-only for reads/writes;
/// shared queries are readable by every Content user but mutable only by the
/// owner. Validation is syntax-only — saved definitions may go stale as
/// document types or permissions change.
/// </summary>
public class SavedQueryService
{
    public const int MaxNameLength = 120;
    public const int MaxCultureLength = 20;
    public const int MaxOwnerNameLength = 255;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SavedQueryStore _store;

    public SavedQueryService(SavedQueryStore store)
    {
        _store = store;
    }

    public SavedQueryListResult List(IUser user) =>
        new(_store.ListVisible(user.Key)
            .Where(r => !SavedAuditService.IsVersion2Json(r.Definition))
            .Select(r => ToResponse(r, user.Key)).ToArray());

    public SavedQueryGetResult Get(IUser user, Guid id)
    {
        var record = _store.GetVisible(id, user.Key);
        // Schema-v2 records are managed through the audit endpoints only.
        return record is null || SavedAuditService.IsVersion2Json(record.Definition)
            ? SavedQueryGetResult.NotFound
            : new SavedQueryGetResult(ToResponse(record, user.Key));
    }

    public SavedQueryCreateResult Create(IUser user, SavedQueryWriteRequest request)
    {
        var errors = Validate(request.Name, request.Query);
        if (errors.Count > 0)
        {
            return SavedQueryCreateResult.ValidationError(errors);
        }

        var utcNow = DateTime.UtcNow;
        var record = new SavedQueryRecord
        {
            Key = Guid.NewGuid(),
            OwnerKey = user.Key,
            Name = request.Name.Trim(),
            OwnerName = OwnerDisplayName(user),
            IsShared = request.IsShared,
            Definition = Serialize(request.Query),
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow,
            Version = 1,
        };
        _store.Create(record);
        return new SavedQueryCreateResult(ToResponse(record, user.Key));
    }

    public SavedQueryUpdateResult Update(IUser user, Guid id, SavedQueryWriteRequest request)
    {
        var errors = Validate(request.Name, request.Query);
        if (request.Version is null or < 1 or >= int.MaxValue)
        {
            errors["version"] = new[] { $"Version must be between 1 and {int.MaxValue - 1}." };
        }

        if (errors.Count > 0)
        {
            return SavedQueryUpdateResult.ValidationError(errors);
        }

        // An owned v2 record cannot be lossily downgraded through this API.
        var owned = _store.GetOwned(id, user.Key);
        if (owned is not null && SavedAuditService.IsVersion2Json(owned.Definition))
        {
            return SavedQueryUpdateResult.Conflict;
        }

        var outcome = _store.UpdateOwned(
            id,
            user.Key,
            request.Name.Trim(),
            request.IsShared,
            Serialize(request.Query),
            OwnerDisplayName(user),
            DateTime.UtcNow,
            request.Version!.Value);

        return outcome.Status switch
        {
            SavedQueryStore.UpdateStatus.NotFound => SavedQueryUpdateResult.NotFound,
            SavedQueryStore.UpdateStatus.Conflict => SavedQueryUpdateResult.Conflict,
            _ => new SavedQueryUpdateResult(SavedQueryUpdateResult.StatusKind.Updated)
            {
                Response = ToResponse(outcome.Record!, user.Key),
            },
        };
    }

    public SavedQueryDeleteResult Delete(IUser user, Guid id, int? version)
    {
        if (version is null or < 1 or >= int.MaxValue)
        {
            return SavedQueryDeleteResult.ValidationError(
                new Dictionary<string, string[]>
                {
                    ["version"] = new[] { $"Version must be between 1 and {int.MaxValue - 1}." },
                });
        }

        var outcome = _store.DeleteOwned(id, user.Key, version.Value);
        return outcome.Status switch
        {
            SavedQueryStore.DeleteStatus.NotFound => SavedQueryDeleteResult.NotFound,
            SavedQueryStore.DeleteStatus.Conflict => SavedQueryDeleteResult.Conflict,
            _ => SavedQueryDeleteResult.Deleted,
        };
    }

    // ---- validation ---------------------------------------------------------

    /// <summary>Syntax-only validation of name and definition.</summary>
    private static Dictionary<string, string[]> Validate(string? name, SavedQueryDefinition? query)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) => errors[field] = new[] { message };

        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            Add("name", "A name is required.");
        }
        else if (trimmedName.Length > MaxNameLength)
        {
            Add("name", $"Name must be {MaxNameLength} characters or fewer.");
        }

        if (query is null)
        {
            Add("query", "A query definition is required.");
            return errors;
        }

        var propertyAlias = query.PropertyAlias?.Trim();
        if (string.IsNullOrEmpty(propertyAlias))
        {
            Add("propertyAlias", "A property alias is required.");
        }
        else if (propertyAlias.Length > PropertyAuditService.MaxAliasLength)
        {
            Add("propertyAlias", $"Property alias must be {PropertyAuditService.MaxAliasLength} characters or fewer.");
        }

        if (query.DocumentTypeAlias?.Trim().Length > PropertyAuditService.MaxAliasLength)
        {
            Add("documentTypeAlias", $"Document type alias must be {PropertyAuditService.MaxAliasLength} characters or fewer.");
        }

        if (string.IsNullOrEmpty(query.Operator) || !AuditValueEvaluator.SupportedOperators.Contains(query.Operator))
        {
            Add("operator", "Operator must be one of: isEmpty, hasText, contains, equals.");
        }

        if (query.SearchValue?.Length > PropertyAuditService.MaxSearchValueLength)
        {
            Add("searchValue", $"Search value must be {PropertyAuditService.MaxSearchValueLength} characters or fewer.");
        }
        else if (query.Operator is not null
            && AuditValueEvaluator.RequiresSearchValue(query.Operator)
            && string.IsNullOrWhiteSpace(query.SearchValue))
        {
            Add("searchValue", $"A non-empty search value is required for the '{query.Operator}' operator.");
        }

        if (query.Culture?.Trim().Length > MaxCultureLength)
        {
            Add("culture", $"Culture must be {MaxCultureLength} characters or fewer.");
        }

        return errors;
    }

    // ---- mapping ------------------------------------------------------------

    private static string Serialize(SavedQueryDefinition definition) =>
        JsonSerializer.Serialize(
            new SavedQueryDefinition
            {
                DocumentTypeAlias = NullIfBlank(definition.DocumentTypeAlias),
                PropertyAlias = definition.PropertyAlias.Trim(),
                Operator = definition.Operator,
                SearchValue = definition.SearchValue,
                Culture = NullIfBlank(definition.Culture),
            },
            JsonOptions);

    private static SavedQueryDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<SavedQueryDefinition>(json, JsonOptions)
        ?? new SavedQueryDefinition { PropertyAlias = string.Empty, Operator = "isEmpty" };

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string OwnerDisplayName(IUser user)
    {
        var name = user.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = user.Username;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "(unnamed)";
        }
        return name.Length > MaxOwnerNameLength ? name[..MaxOwnerNameLength] : name;
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value;

    private static SavedQueryResponse ToResponse(SavedQueryRecord record, Guid currentUserKey) =>
        new()
        {
            Id = record.Key,
            Name = record.Name,
            IsShared = record.IsShared,
            IsOwner = record.OwnerKey == currentUserKey,
            OwnerName = record.OwnerName,
            Query = Deserialize(record.Definition),
            Version = record.Version,
            UpdatedDate = AsUtc(record.UpdatedUtc),
        };

    // ---- results ------------------------------------------------------------

    public sealed record SavedQueryListResult(IReadOnlyList<SavedQueryResponse> Items);

    public record SavedQueryGetResult(SavedQueryResponse? Response)
    {
        public static readonly SavedQueryGetResult NotFound = new((SavedQueryResponse?)null);
    }

    public record SavedQueryCreateResult(
        SavedQueryResponse? Response = null,
        Dictionary<string, string[]>? Errors = null)
    {
        public static SavedQueryCreateResult ValidationError(Dictionary<string, string[]> errors) => new(null, errors);
    }

    public record SavedQueryUpdateResult(SavedQueryUpdateResult.StatusKind Status)
    {
        public enum StatusKind { Updated, NotFound, Conflict, Invalid }

        public static readonly SavedQueryUpdateResult NotFound = new(StatusKind.NotFound);
        public static readonly SavedQueryUpdateResult Conflict = new(StatusKind.Conflict);
        public static SavedQueryUpdateResult ValidationError(Dictionary<string, string[]> errors) =>
            new(StatusKind.Invalid) { Errors = errors };

        public SavedQueryResponse? Response { get; init; }
        public Dictionary<string, string[]>? Errors { get; init; }
    }

    public record SavedQueryDeleteResult(SavedQueryDeleteResult.StatusKind Status)
    {
        public enum StatusKind { Deleted, NotFound, Conflict, Invalid }

        public static readonly SavedQueryDeleteResult Deleted = new(StatusKind.Deleted);
        public static readonly SavedQueryDeleteResult NotFound = new(StatusKind.NotFound);
        public static readonly SavedQueryDeleteResult Conflict = new(StatusKind.Conflict);
        public static SavedQueryDeleteResult ValidationError(Dictionary<string, string[]> errors) =>
            new(StatusKind.Invalid) { Errors = errors };

        public Dictionary<string, string[]>? Errors { get; init; }
    }
}

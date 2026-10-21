using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PropertyAuditTool.Models;
using PropertyAuditTool.Persistence;
using Umbraco.Cms.Core.Models.Membership;

namespace PropertyAuditTool.Services;

/// <summary>
/// Saved-audit (schema v2) semantics on top of the shared
/// <c>propertyAuditSavedQuery</c> store plus per-viewer dashboard preferences.
/// Legacy v1 definitions are adapted on reads without rewriting records.
/// Validation is syntax-only; saved definitions may go stale as document
/// types, element types, or permissions change.
/// </summary>
public class SavedAuditService
{
    public const int MaxTitleLength = 120;
    public const int MaxDescriptionLength = 2000;
    public const int MaxCultureLength = 20;
    public const int MaxAliasLength = 255;
    public const int MaxFilterTextLength = 2000;
    public const int MaxTypes = 32;
    public const int MaxColumns = 16;
    public const int MaxTableTypes = 512;
    public const int MaxTableColumns = 512;
    public static readonly string[] BaseColumns = ["kind", "typeName", "content", "culture", "location"];
    private static readonly ISet<string> RequiredBaseColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "kind", "typeName", "content",
    };
    public const int MaxIconLength = 64;
    public const int MaxOwnerNameLength = 255;
    public const string DefaultIcon = "icon-search";
    // Keep semantic theme tokens within the existing seven-character column.
    public static readonly string[] ThemeColours = ["@grey", "@green", "@blue"];

    private static readonly Regex IconPattern = new(@"^icon-[a-z0-9-]+$", RegexOptions.Compiled);
    private static readonly Regex ColourPattern = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    private static readonly ISet<string> TypeKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "document", "element",
    };

    private static readonly ISet<string> ValueTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "text", "number", "date", "boolean",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SavedQueryStore _store;

    public SavedAuditService(SavedQueryStore store)
    {
        _store = store;
    }

    // ---- definition detection / adaptation -----------------------------------

    /// <summary>True when the serialized definition declares schemaVersion 2.</summary>
    public static bool IsVersion2Json(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var version)
                && version.ValueKind == JsonValueKind.Number
                && version.GetInt32() is 2 or 3;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adapts a legacy v1 definition to the v2 model on reads. The stored
    /// record is never rewritten.
    /// </summary>
    public static AuditDefinition AdaptLegacyDefinition(string json)
    {
        var legacy = JsonSerializer.Deserialize<SavedQueryDefinition>(json, JsonOptions)
            ?? new SavedQueryDefinition { PropertyAlias = string.Empty, Operator = "isEmpty" };

        var definition = new AuditDefinition
        {
            SchemaVersion = 2,
            AllDocumentTypes = string.IsNullOrWhiteSpace(legacy.DocumentTypeAlias),
            Culture = legacy.Culture,
        };

        if (!string.IsNullOrWhiteSpace(legacy.DocumentTypeAlias))
        {
            definition.Types.Add(new AuditTypeSelection
            {
                Kind = "document",
                Alias = legacy.DocumentTypeAlias.Trim(),
            });
        }

        definition.Columns.Add(new AuditColumnDefinition
        {
            Alias = legacy.PropertyAlias,
            ValueType = "text",
            Filter = new AuditColumnFilter
            {
                Operator = legacy.Operator,
                Value = legacy.SearchValue,
            },
        });
        return definition;
    }

    /// <summary>Deserializes a stored definition (v2 or adapted legacy v1).</summary>
    public static AuditDefinition DeserializeDefinition(string json) =>
        IsVersion2Json(json)
            ? (JsonSerializer.Deserialize<AuditDefinition>(json, JsonOptions) ?? new AuditDefinition())
            : AdaptLegacyDefinition(json);

    private static string Serialize(AuditDefinition definition) =>
        JsonSerializer.Serialize(
            new AuditDefinition
            {
                SchemaVersion = definition.SchemaVersion,
                AllDocumentTypes = definition.AllDocumentTypes,
                AllElementTypes = definition.AllElementTypes,
                Types = definition.Types,
                Columns = definition.Columns,
                Culture = definition.Culture,
                AllCultures = definition.AllCultures,
                Cultures = definition.Cultures,
                RowFilters = definition.RowFilters,
                HiddenBaseColumns = definition.HiddenBaseColumns,
            },
            JsonOptions);

    // ---- reads -----------------------------------------------------------------

    public SavedAuditListResult List(IUser user)
    {
        var preferences = _store.ListPreferences(user.Key)
            .ToDictionary(preference => preference.AuditKey);
        var items = _store.ListVisible(user.Key)
            .Select(record => ToResponse(record, user.Key, preferences))
            .ToArray();
        return new SavedAuditListResult(items);
    }

    public SavedAuditGetResult Get(IUser user, Guid id)
    {
        var record = _store.GetVisible(id, user.Key);
        if (record is null)
        {
            return SavedAuditGetResult.NotFound;
        }

        var preference = _store.GetPreference(
            SavedQueryStore.DerivePreferenceKey(user.Key, id), user.Key, id);
        return new SavedAuditGetResult(ToResponse(record, user.Key, preference));
    }

    // ---- writes ------------------------------------------------------------

    /// <summary>
    /// Deep clone with normalized structure: trimmed type/column aliases and
    /// culture (blank becomes null), duplicates removed by (kind, alias) and by
    /// column alias retaining the first occurrence. Filter values and
    /// expressions are preserved verbatim.
    /// </summary>
    public static AuditDefinition NormalizeDefinition(AuditDefinition definition)
    {
        var normalized = new AuditDefinition
        {
            SchemaVersion = definition.SchemaVersion,
            AllDocumentTypes = definition.AllDocumentTypes,
            AllElementTypes = definition.AllElementTypes,
            Culture = definition.Culture?.Trim() is { Length: > 0 } culture ? culture : null,
            AllCultures = definition.AllCultures,
            Cultures = definition.Cultures?.Select(culture => culture?.Trim() ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RowFilters = (definition.RowFilters ?? []).Select(column => column is null ? null! : new AuditColumnDefinition
            {
                Alias = column.Alias?.Trim() ?? "",
                ValueType = column.ValueType,
                Filter = column.Filter is null ? null : new AuditColumnFilter
                {
                    Operator = column.Filter.Operator,
                    Value = column.Filter.Value,
                    Expression = column.Filter.Expression,
                },
            }).ToList(),
            HiddenBaseColumns = (definition.HiddenBaseColumns ?? [])
                .Select(alias => alias?.Trim() ?? "")
                .Where(alias => !RequiredBaseColumns.Contains(alias))
                .Distinct(StringComparer.Ordinal).ToList(),
        };

        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in definition.Types ?? Enumerable.Empty<AuditTypeSelection?>())
        {
            if (selection is null)
            {
                normalized.Types.Add(null!);
                continue;
            }

            var alias = selection.Alias?.Trim() ?? string.Empty;
            var kind = selection.Kind?.Trim() ?? string.Empty;
            if (seenTypes.Add($"{kind}:{alias}"))
            {
                normalized.Types.Add(new AuditTypeSelection { Kind = kind, Alias = alias });
            }
        }

        var seenColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in definition.Columns ?? Enumerable.Empty<AuditColumnDefinition?>())
        {
            if (column is null)
            {
                normalized.Columns.Add(null!);
                continue;
            }

            var alias = column.Alias?.Trim() ?? string.Empty;
            if (seenColumns.Add(alias))
            {
                normalized.Columns.Add(new AuditColumnDefinition
                {
                    Alias = alias,
                    ValueType = column.ValueType,
                    Filter = column.Filter is null
                        ? null
                        : new AuditColumnFilter
                        {
                            Operator = column.Filter.Operator,
                            Value = column.Filter.Value,
                            Expression = column.Filter.Expression,
                        },
                });
            }
        }

        return normalized;
    }

    public SavedAuditCreateResult Create(IUser user, AuditWriteRequest request)
    {
        var errors = ValidateWrite(request.Title, request.Description);
        var normalized = request.Definition is null ? null : NormalizeDefinition(request.Definition);
        foreach (var (field, messages) in ValidateDefinition(normalized))
        {
            errors[field] = messages;
        }

        if (errors.Count > 0)
        {
            return SavedAuditCreateResult.ValidationError(errors);
        }

        var utcNow = DateTime.UtcNow;
        var record = new SavedQueryRecord
        {
            Key = Guid.NewGuid(),
            OwnerKey = user.Key,
            Name = request.Title.Trim(),
            Description = NormalizeOptional(request.Description, MaxDescriptionLength),
            OwnerName = OwnerDisplayName(user),
            IsShared = request.IsShared,
            Definition = Serialize(normalized!),
            CreatedUtc = utcNow,
            UpdatedUtc = utcNow,
            Version = 1,
        };
        _store.Create(record);
        return new SavedAuditCreateResult(ToResponse(record, user.Key, (DashboardPreferenceRecord?)null));
    }

    public SavedAuditUpdateResult Update(IUser user, Guid id, AuditWriteRequest request)
    {
        var errors = ValidateWrite(request.Title, request.Description);
        var normalized = request.Definition is null ? null : NormalizeDefinition(request.Definition);
        foreach (var (field, messages) in ValidateDefinition(normalized))
        {
            errors[field] = messages;
        }

        if (request.Version is null or < 1 or >= int.MaxValue)
        {
            errors["version"] = new[] { $"Version must be between 1 and {int.MaxValue - 1}." };
        }

        if (errors.Count > 0)
        {
            return SavedAuditUpdateResult.ValidationError(errors);
        }

        var serializedDefinition = Serialize(normalized!);
        var existing = _store.GetOwned(id, user.Key);
        var definitionChanged = !string.Equals(
            existing?.Definition, serializedDefinition, StringComparison.Ordinal);
        var outcome = _store.UpdateAuditOwned(
            id,
            user.Key,
            request.Title.Trim(),
            NormalizeOptional(request.Description, MaxDescriptionLength),
            request.IsShared,
            serializedDefinition,
            OwnerDisplayName(user),
            DateTime.UtcNow,
            request.Version!.Value);

        if (outcome.Status == SavedQueryStore.UpdateStatus.Updated && !definitionChanged)
        {
            _store.SyncCompletedSummaryDefinitionVersion(id, outcome.Record!.Version);
        }

        return outcome.Status switch
        {
            SavedQueryStore.UpdateStatus.NotFound => SavedAuditUpdateResult.NotFound,
            SavedQueryStore.UpdateStatus.Conflict => SavedAuditUpdateResult.Conflict,
            _ => new SavedAuditUpdateResult(SavedAuditUpdateResult.StatusKind.Updated)
            {
                Response = ToResponse(
                    outcome.Record!, user.Key,
                    _store.GetPreference(SavedQueryStore.DerivePreferenceKey(user.Key, id), user.Key, id)),
            },
        };
    }

    public SavedAuditDeleteResult Delete(IUser user, Guid id, int? version)
    {
        if (version is null or < 1 or >= int.MaxValue)
        {
            return SavedAuditDeleteResult.ValidationError(
                new Dictionary<string, string[]>
                {
                    ["version"] = new[] { $"Version must be between 1 and {int.MaxValue - 1}." },
                });
        }

        var outcome = _store.DeleteOwned(id, user.Key, version.Value);
        return outcome.Status switch
        {
            SavedQueryStore.DeleteStatus.NotFound => SavedAuditDeleteResult.NotFound,
            SavedQueryStore.DeleteStatus.Conflict => SavedAuditDeleteResult.Conflict,
            _ => SavedAuditDeleteResult.Deleted,
        };
    }

    /// <summary>
    /// Sets the current viewer's personal dashboard preference. Requires read
    /// access to the audit only — a shared audit may be pinned by a non-owner.
    /// </summary>
    public SavedAuditPreferenceResult SetDashboardPreference(
        IUser user, Guid id, AuditDashboardPreferenceWriteRequest request)
    {
        var record = _store.GetVisible(id, user.Key);
        if (record is null)
        {
            return SavedAuditPreferenceResult.NotFound;
        }

        var errors = new Dictionary<string, string[]>();
        var icon = request.Icon?.Trim();
        if (string.IsNullOrEmpty(icon))
        {
            icon = DefaultIcon;
        }
        else if (icon.Length > MaxIconLength || !IconPattern.IsMatch(icon))
        {
            errors["icon"] = new[] { $"Icon must match 'icon-[a-z0-9-]+' and be {MaxIconLength} characters or fewer." };
        }

        var colour = request.Colour?.Trim();
        if (colour?.StartsWith("theme-", StringComparison.Ordinal) == true)
            colour = "@" + colour["theme-".Length..];
        if (string.IsNullOrEmpty(colour))
        {
            colour = null;
        }
        else if (!ColourPattern.IsMatch(colour) && !ThemeColours.Contains(colour))
        {
            errors["colour"] = new[] { "Choose an Umbraco theme colour or a '#RRGGBB' colour." };
        }

        if (errors.Count > 0)
        {
            return SavedAuditPreferenceResult.ValidationError(errors);
        }

        if (request.IsPinned && colour is null)
        {
            var previous = _store.GetPreference(SavedQueryStore.DerivePreferenceKey(user.Key, id), user.Key, id);
            colour = previous?.Colour ?? ThemeColours[Random.Shared.Next(ThemeColours.Length)];
        }
        var preference = _store.SetPreference(user.Key, id, request.IsPinned, icon!, colour);
        return new SavedAuditPreferenceResult(SavedAuditPreferenceResult.StatusKind.Updated)
        {
            Response = ToResponse(record, user.Key, preference),
        };
    }

    // ---- validation -----------------------------------------------------------

    /// <summary>Title/description write validation.</summary>
    private static Dictionary<string, string[]> ValidateWrite(string? title, string? description)
    {
        var errors = new Dictionary<string, string[]>();
        var trimmedTitle = title?.Trim();
        if (string.IsNullOrEmpty(trimmedTitle))
        {
            errors["title"] = new[] { "A title is required." };
        }
        else if (trimmedTitle.Length > MaxTitleLength)
        {
            errors["title"] = new[] { $"Title must be {MaxTitleLength} characters or fewer." };
        }

        if (description?.Trim().Length > MaxDescriptionLength)
        {
            errors["description"] = new[] { $"Description must be {MaxDescriptionLength} characters or fewer." };
        }

        return errors;
    }

    /// <summary>
    /// Syntax-only validation of a normalized definition; reusable by draft
    /// run creation. Expects <see cref="NormalizeDefinition"/> output — counts
    /// are checked after deduplication, duplicates are not errors.
    /// </summary>
    public static Dictionary<string, string[]> ValidateDefinition(AuditDefinition? definition)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string field, string message) => errors[field] = new[] { message };

        if (definition is null)
        {
            Add("definition", "An audit definition is required.");
            return errors;
        }

        if (definition.SchemaVersion is not (2 or 3))
        {
            Add("definition.schemaVersion", "Schema version must be 2 or 3.");
        }

        var types = definition.Types ?? new List<AuditTypeSelection?>();
        var maxTypes = definition.SchemaVersion >= 3 ? MaxTableTypes : MaxTypes;
        if (types.Count > maxTypes)
        {
            Add("definition.types", $"At most {maxTypes} types may be selected.");
        }

        for (var i = 0; i < types.Count; i++)
        {
            var selection = types[i];
            var field = $"definition.types[{i}]";
            if (selection is null)
            {
                Add(field, "Type selection is required.");
                continue;
            }

            if (string.IsNullOrEmpty(selection.Kind) || !TypeKinds.Contains(selection.Kind))
            {
                Add($"{field}.kind", "Kind must be 'document' or 'element'.");
            }

            var alias = selection.Alias?.Trim();
            if (string.IsNullOrEmpty(alias))
            {
                Add($"{field}.alias", "A type alias is required.");
            }
            else if (alias.Length > MaxAliasLength)
            {
                Add($"{field}.alias", $"Type alias must be {MaxAliasLength} characters or fewer.");
            }
        }

        if (definition.SchemaVersion < 3 && !definition.AllDocumentTypes && !definition.AllElementTypes && types.Count == 0)
        {
            Add("definition.types", "Select at least one type, or enable all document types.");
        }

        var columns = definition.Columns ?? new List<AuditColumnDefinition?>();
        var maxColumns = definition.SchemaVersion >= 3 ? MaxTableColumns : MaxColumns;
        if ((definition.SchemaVersion < 3 && columns.Count == 0) || columns.Count > maxColumns)
        {
            Add("definition.columns", $"At most {maxColumns} columns are allowed; legacy audits require at least one.");
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var field = $"definition.columns[{i}]";
            if (column is null)
            {
                Add(field, "Column is required.");
                continue;
            }

            var alias = column.Alias?.Trim();
            if (string.IsNullOrEmpty(alias))
            {
                Add($"{field}.alias", "A column alias is required.");
            }
            else if (alias.Length > MaxAliasLength)
            {
                Add($"{field}.alias", $"Column alias must be {MaxAliasLength} characters or fewer.");
            }

            if (string.IsNullOrEmpty(column.ValueType) || !ValueTypes.Contains(column.ValueType))
            {
                Add($"{field}.valueType", "Value type must be one of: text, number, date, boolean.");
            }

            var filter = column.Filter;
            if (filter is null)
            {
                continue;
            }

            if (!AuditFilterCompiler.TryCompile(
                    column.ValueType ?? string.Empty,
                    filter.Operator ?? string.Empty,
                    filter.Value,
                    filter.Expression,
                    out _,
                    out var filterError))
            {
                Add($"{field}.filter", filterError ?? "Invalid filter.");
            }
        }

        if (definition.Culture?.Trim().Length > MaxCultureLength)
        {
            Add("definition.culture", $"Culture must be {MaxCultureLength} characters or fewer.");
        }

        if (definition.Cultures?.Count > 256
            || definition.Cultures?.Any(culture => culture is null || culture.Length > MaxCultureLength) == true)
        {
            Add("definition.cultures", "Select at most 256 cultures with codes of 20 characters or fewer.");
        }
        if (definition.HiddenBaseColumns?.Any(alias => !BaseColumns.Contains(alias)) == true)
            Add("definition.hiddenBaseColumns", "Unknown base column.");
        if (definition.RowFilters?.Count > BaseColumns.Length)
            Add("definition.rowFilters", "Too many base-column filters.");
        foreach (var column in definition.RowFilters ?? [])
        {
            if (column is null || !BaseColumns.Contains(column.Alias) || column.ValueType != "text")
            {
                Add("definition.rowFilters", "Base-column filters require a known column and text value type.");
                continue;
            }
            if (column.Filter is { } filter
                && !AuditFilterCompiler.TryCompile("text", filter.Operator, filter.Value, filter.Expression, out _, out var error))
                Add("definition.rowFilters", error ?? "Invalid base-column filter.");
        }

        return errors;
    }

    // ---- mapping ---------------------------------------------------------------

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
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

    private static DateTime? AsUtc(DateTime? value) =>
        value is null ? null : AsUtc(value.Value);

    private static AuditDashboardPreferenceResponse ToDashboardResponse(
        DashboardPreferenceRecord? preference, int definitionVersion) =>
        new()
        {
            IsPinned = preference?.IsPinned ?? false,
            Icon = preference?.Icon ?? DefaultIcon,
            Colour = preference?.Colour,
            LastCount = preference?.LastCount,
            LastCompletedUtc = AsUtc(preference?.LastCompletedUtc),
            IsStale = preference?.LastDefinitionVersion is not null
                && preference.LastDefinitionVersion != definitionVersion,
        };

    private static AuditResponse ToResponse(
        SavedQueryRecord record,
        Guid currentUserKey,
        DashboardPreferenceRecord? preference) =>
        new()
        {
            Id = record.Key,
            Title = record.Name,
            Description = record.Description,
            IsShared = record.IsShared,
            IsOwner = record.OwnerKey == currentUserKey,
            OwnerName = record.OwnerName,
            Definition = DeserializeDefinition(record.Definition),
            Version = record.Version,
            CreatedDate = AsUtc(record.CreatedUtc),
            UpdatedDate = AsUtc(record.UpdatedUtc),
            Dashboard = ToDashboardResponse(preference, record.Version),
        };

    private static AuditResponse ToResponse(
        SavedQueryRecord record,
        Guid currentUserKey,
        IReadOnlyDictionary<Guid, DashboardPreferenceRecord> preferences)
    {
        preferences.TryGetValue(record.Key, out var preference);
        // Preferences of invisible audits cannot reach this method: records come
        // from the visible list, so lookup by record key only.
        return ToResponse(record, currentUserKey, preference);
    }

    // ---- results -----------------------------------------------------------------

    public sealed record SavedAuditListResult(IReadOnlyList<AuditResponse> Items);

    public record SavedAuditGetResult(AuditResponse? Response)
    {
        public static readonly SavedAuditGetResult NotFound = new((AuditResponse?)null);
    }

    public record SavedAuditCreateResult(
        AuditResponse? Response = null,
        Dictionary<string, string[]>? Errors = null)
    {
        public static SavedAuditCreateResult ValidationError(Dictionary<string, string[]> errors) => new(null, errors);
    }

    public record SavedAuditUpdateResult(SavedAuditUpdateResult.StatusKind Status)
    {
        public enum StatusKind { Updated, NotFound, Conflict, Invalid }

        public static readonly SavedAuditUpdateResult NotFound = new(StatusKind.NotFound);
        public static readonly SavedAuditUpdateResult Conflict = new(StatusKind.Conflict);
        public static SavedAuditUpdateResult ValidationError(Dictionary<string, string[]> errors) =>
            new(StatusKind.Invalid) { Errors = errors };

        public AuditResponse? Response { get; init; }
        public Dictionary<string, string[]>? Errors { get; init; }
    }

    public record SavedAuditDeleteResult(SavedAuditDeleteResult.StatusKind Status)
    {
        public enum StatusKind { Deleted, NotFound, Conflict, Invalid }

        public static readonly SavedAuditDeleteResult Deleted = new(StatusKind.Deleted);
        public static readonly SavedAuditDeleteResult NotFound = new(StatusKind.NotFound);
        public static readonly SavedAuditDeleteResult Conflict = new(StatusKind.Conflict);
        public static SavedAuditDeleteResult ValidationError(Dictionary<string, string[]> errors) =>
            new(StatusKind.Invalid) { Errors = errors };

        public Dictionary<string, string[]>? Errors { get; init; }
    }

    public record SavedAuditPreferenceResult(SavedAuditPreferenceResult.StatusKind Status)
    {
        public enum StatusKind { Updated, NotFound, Invalid }

        public static readonly SavedAuditPreferenceResult NotFound = new(StatusKind.NotFound);
        public static SavedAuditPreferenceResult ValidationError(Dictionary<string, string[]> errors) =>
            new(StatusKind.Invalid) { Errors = errors };

        public AuditResponse? Response { get; init; }
        public Dictionary<string, string[]>? Errors { get; init; }
    }
}

using System.Text.Json;

namespace PropertyAuditTool.Services;

public sealed record AuditBlockInstance(
    Guid Key, Guid ContentTypeKey, string Path, bool IsSettings, JsonElement Data)
{
    public IReadOnlyList<string?>? ExposedCultures { get; init; }
}

public sealed record AuditBlockReadResult(
    IReadOnlyList<AuditBlockInstance> Instances, IReadOnlyList<string> Warnings);

/// <summary>Reads layout-referenced Block List/Grid instances, not orphan data.</summary>
public static class AuditBlockReader
{
    public const int MaxCharacters = 2_000_000;
    public const int MaxInstances = 10_000;
    public const int MaxLayoutDepth = 32;

    public static AuditBlockReadResult Read(string? json, string editorAlias)
    {
        var instances = new List<AuditBlockInstance>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return new(instances, warnings);
        if (editorAlias is not ("Umbraco.BlockList" or "Umbraco.BlockGrid"))
            return new(instances, ["Unsupported block editor."]);
        if (json.Length > MaxCharacters) return new(instances, ["Block value exceeds the safety limit."]);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(instances, ["Block value is not an object."]);
            var content = Index(root, "contentData", warnings);
            var settings = Index(root, "settingsData", warnings);
            if (!root.TryGetProperty("layout", out var layout)
                || layout.ValueKind != JsonValueKind.Object
                || !layout.TryGetProperty(editorAlias, out var items))
            {
                if (content.Count > 0 || settings.Count > 0)
                    warnings.Add("Block data has no matching layout.");
                return new(instances, warnings);
            }
            Dictionary<Guid, IReadOnlyList<string?>>? exposures = null;
            if (root.TryGetProperty("expose", out var expose) && expose.ValueKind == JsonValueKind.Array)
            {
                exposures = expose.EnumerateArray()
                    .Where(entry => Key(entry, "contentKey", "contentUdi", out _))
                    .GroupBy(entry => { Key(entry, "contentKey", "contentUdi", out var key); return key; })
                    .ToDictionary(group => group.Key, group => (IReadOnlyList<string?>)group
                        .Select(entry => entry.TryGetProperty("culture", out var culture)
                            && culture.ValueKind == JsonValueKind.String ? culture.GetString() : null)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            Walk(items, editorAlias, 0, content, settings, instances, warnings, exposures);
        }
        catch (JsonException)
        {
            warnings.Add("Block value contains invalid JSON or exceeds the depth limit.");
        }
        return new(instances, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<Guid, JsonElement> Index(
        JsonElement root, string property, List<string> warnings)
    {
        var result = new Dictionary<Guid, JsonElement>();
        if (!root.TryGetProperty(property, out var values)) return result;
        if (values.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("Block data collection is not an array.");
            return result;
        }
        foreach (var value in values.EnumerateArray())
        {
            if (!Key(value, "key", "udi", out var key))
            {
                warnings.Add("Block data has an invalid key.");
                continue;
            }
            if (!result.TryAdd(key, value)) warnings.Add("Block data has a duplicate key.");
        }
        return result;
    }

    private static void Walk(
        JsonElement items, string path, int depth,
        Dictionary<Guid, JsonElement> content, Dictionary<Guid, JsonElement> settings,
        List<AuditBlockInstance> instances, List<string> warnings,
        Dictionary<Guid, IReadOnlyList<string?>>? exposures)
    {
        if (depth > MaxLayoutDepth)
        {
            warnings.Add("Block layout exceeds the nesting limit.");
            return;
        }
        if (items.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("Block layout items are not an array.");
            return;
        }
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (instances.Count >= MaxInstances)
            {
                warnings.Add("Block layout exceeds the instance limit.");
                return;
            }
            var location = $"{path}[{index++}]";
            Append(item, "contentKey", "contentUdi", content, location + "/content", false, instances, warnings, exposures);
            Append(item, "settingsKey", "settingsUdi", settings, location + "/settings", true, instances, warnings, exposures);
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("areas", out var areas))
            {
                if (areas.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add("Block grid areas are not an array.");
                    continue;
                }
                var areaIndex = 0;
                foreach (var area in areas.EnumerateArray())
                {
                    if (area.ValueKind == JsonValueKind.Object && area.TryGetProperty("items", out var children))
                        Walk(children, $"{location}/area[{areaIndex}]", depth + 1, content, settings, instances, warnings, exposures);
                    else warnings.Add("Block grid area has no items.");
                    areaIndex++;
                }
            }
        }
    }

    private static void Append(
        JsonElement item, string keyName, string udiName, Dictionary<Guid, JsonElement> index,
        string path, bool isSettings, List<AuditBlockInstance> instances, List<string> warnings,
        Dictionary<Guid, IReadOnlyList<string?>>? exposures)
    {
        if (!Key(item, keyName, udiName, out var key))
        {
            if (!isSettings) warnings.Add("Block layout has an invalid content key.");
            return;
        }
        if (!index.TryGetValue(key, out var data)
            || !Key(data, "contentTypeKey", null, out var typeKey))
        {
            warnings.Add("Block layout refers to missing or invalid data.");
            return;
        }
        if (instances.Count >= MaxInstances)
        {
            warnings.Add("Block layout exceeds the instance limit.");
            return;
        }
        Key(item, "contentKey", "contentUdi", out var exposureKey);
        instances.Add(new(key, typeKey, path, isSettings, data.Clone())
        {
            ExposedCultures = exposures is null ? null : exposures.GetValueOrDefault(exposureKey) ?? [],
        });
    }

    private static bool Key(JsonElement value, string name, string? legacyName, out Guid key)
    {
        key = default;
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out key) && key != Guid.Empty) return true;
        if (legacyName is not null && value.TryGetProperty(legacyName, out property)
            && property.ValueKind == JsonValueKind.String)
        {
            var udi = property.GetString();
            if (udi?.StartsWith("umb://element/", StringComparison.OrdinalIgnoreCase) == true)
                return Guid.TryParse(udi["umb://element/".Length..], out key) && key != Guid.Empty;
        }
        return false;
    }

    public static object? PropertyValue(JsonElement data, string alias, string? culture)
    {
        if (data.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object
                    || !value.TryGetProperty("alias", out var name) || name.GetString() != alias)
                    continue;
                var actualCulture = value.TryGetProperty("culture", out var language)
                    && language.ValueKind == JsonValueKind.String ? language.GetString() : null;
                var hasSegment = value.TryGetProperty("segment", out var segment)
                    && segment.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(segment.GetString());
                if (!hasSegment && string.Equals(actualCulture, culture, StringComparison.OrdinalIgnoreCase))
                    return value.TryGetProperty("value", out var raw) ? raw.Clone() : null;
            }
            return null;
        }
        // Legacy invariant block payloads placed property values directly on data.
        return culture is null && data.TryGetProperty(alias, out var legacy) ? legacy.Clone() : null;
    }
}

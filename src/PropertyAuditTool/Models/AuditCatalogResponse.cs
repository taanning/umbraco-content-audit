namespace PropertyAuditTool.Models;

/// <summary>
/// V2 audit builder metadata: document AND element types with their composed,
/// viewer-readable properties plus the cultures the current user may access.
/// </summary>
public sealed class AuditCatalogResponse
{
    public required IReadOnlyList<PropertyAuditTypeModel> Types { get; init; }

    public required IReadOnlyList<LanguageModel> Languages { get; init; }
}

public sealed class PropertyAuditTypeModel
{
    /// <summary>"document" or "element".</summary>
    public required string Kind { get; init; }

    public required string Alias { get; init; }

    public required string Name { get; init; }

    public required Guid Unique { get; init; }

    public required bool VariesByCulture { get; init; }

    public required IReadOnlyList<AuditCatalogPropertyModel> Properties { get; init; }
}

public sealed class AuditCatalogPropertyModel
{
    public required string Alias { get; init; }

    public required string Name { get; init; }

    public required string EditorAlias { get; init; }

    /// <summary>text | number | date | boolean — derived from editor alias and storage type.</summary>
    public required string ValueTypeHint { get; init; }

    public required bool VariesByCulture { get; init; }

    public required bool VariesBySegment { get; init; }
}

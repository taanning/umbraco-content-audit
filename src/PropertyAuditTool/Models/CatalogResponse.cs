namespace PropertyAuditTool.Models;

/// <summary>
/// Audit metadata: non-element document types with their composed properties and
/// the languages the current user may access. Contains no content nodes or values.
/// </summary>
public sealed class CatalogResponse
{
    public required IReadOnlyList<DocumentTypeModel> DocumentTypes { get; init; }

    public required IReadOnlyList<LanguageModel> Languages { get; init; }
}

public sealed class DocumentTypeModel
{
    public required string Alias { get; init; }

    public required string Name { get; init; }

    public required bool VariesByCulture { get; init; }

    public required IReadOnlyList<PropertyTypeModel> Properties { get; init; }
}

public sealed class PropertyTypeModel
{
    public required string Alias { get; init; }

    public required string Name { get; init; }

    public required string EditorAlias { get; init; }

    public required bool VariesByCulture { get; init; }

    public required bool VariesBySegment { get; init; }
}

public sealed class LanguageModel
{
    public required string IsoCode { get; init; }

    public required string Name { get; init; }

    public required bool IsDefault { get; init; }
}

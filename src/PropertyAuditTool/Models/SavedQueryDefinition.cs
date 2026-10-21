namespace PropertyAuditTool.Models;

/// <summary>
/// The five filter fields of a saved audit query. Never contains paging,
/// results, or property values.
/// </summary>
public class SavedQueryDefinition
{
    public string? DocumentTypeAlias { get; set; }

    public required string PropertyAlias { get; set; }

    public required string Operator { get; set; }

    public string? SearchValue { get; set; }

    public string? Culture { get; set; }
}

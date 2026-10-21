namespace PropertyAuditTool.Models;

/// <summary>
/// A single matched document. Never contains the audited property value.
/// </summary>
public sealed class AuditItem
{
    public required int NodeId { get; init; }

    public required Guid Unique { get; init; }

    public required string Name { get; init; }

    public required string DocumentTypeAlias { get; init; }

    public required DateTime UpdatedDate { get; init; }

    /// <summary>"Empty" or "Filled" — whether the stored raw value is whitespace-only.</summary>
    public required string PropertyStatus { get; init; }

    /// <summary>Backoffice edit route for the document.</summary>
    public required string EditUrl { get; init; }
}

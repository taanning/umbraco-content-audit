namespace PropertyAuditTool.Models;

/// <summary>
/// Request body for POST property-audit/query.
/// </summary>
/// <remarks>
/// Skip is a raw candidate scan offset, not a matched-result offset.
/// </remarks>
public sealed class AuditQueryRequest
{
    /// <summary>Optional document type alias. Null/empty means all document types.</summary>
    public string? DocumentTypeAlias { get; set; }

    /// <summary>Required property alias to audit.</summary>
    public string? PropertyAlias { get; set; }

    /// <summary>Required operator: isEmpty, hasText, contains or equals (case-sensitive).</summary>
    public string? Operator { get; set; }

    /// <summary>Optional search value, required for contains/equals.</summary>
    public string? SearchValue { get; set; }

    /// <summary>Optional culture ISO code. Required when the audited type/property varies by culture.</summary>
    public string? Culture { get; set; }

    /// <summary>Raw candidate scan offset.</summary>
    public long Skip { get; set; }

    /// <summary>Maximum number of matches to return (1-200).</summary>
    public int Take { get; set; } = 50;
}

namespace PropertyAuditTool.Models;

/// <summary>
/// Result of a query request. <see cref="NextSkip"/> is a raw scan continuation
/// offset, not a match count; pagination is not a database snapshot.
/// </summary>
public sealed class AuditQueryResponse
{
    public required IReadOnlyList<AuditItem> Items { get; init; }

    /// <summary>Raw candidate offset to resume from, or null when the candidate set is exhausted.</summary>
    public long? NextSkip { get; init; }

    /// <summary>Authorized, non-trashed documents actually inspected in this request (not a total match count).</summary>
    public required long ScannedCount { get; init; }
}

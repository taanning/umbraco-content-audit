namespace PropertyAuditTool.Models;

/// <summary>Transient audit-run progress; totalCount is non-null only when
/// the run completed with an exact result.</summary>
public sealed class AuditRunProgress
{
    public required Guid RunId { get; init; }

    /// <summary>running | completed | partial | limited | cancelled.</summary>
    public required string Status { get; init; }

    public required long ScannedCount { get; init; }

    /// <summary>Observed retained matching rows; not a full total unless completed.</summary>
    public required long MatchedCount { get; init; }

    public long? TotalCount { get; init; }

    public required DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed class AuditRunCell
{
    public required string Alias { get; init; }

    /// <summary>notApplicable | restricted | empty | filled.</summary>
    public required string Status { get; init; }

    public string? Value { get; init; }

    public bool Truncated { get; init; }
}

public sealed class AuditRunRow
{
    public required string RowKey { get; init; }

    /// <summary>document | element.</summary>
    public required string Kind { get; init; }

    public required string TypeAlias { get; init; }

    public required string TypeName { get; init; }

    public required Guid OwnerDocumentKey { get; init; }

    public required string OwnerDocumentName { get; init; }

    public required string Location { get; init; }

    public string? Culture { get; init; }

    public required string EditUrl { get; init; }

    public required IReadOnlyList<AuditRunCell> Cells { get; init; }
}

public sealed class AuditRunResults
{
    public required AuditRunProgress Progress { get; init; }

    /// <summary>Selected column aliases in definition order.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<AuditRunRow> Items { get; init; }

    public required int TotalItems { get; init; }

    public required int Skip { get; init; }

    public required int Take { get; init; }
}

public sealed class AuditRunCreateRequest
{
    /// <summary>Draft definition; ignored when <see cref="AuditId"/> is supplied.</summary>
    public AuditDefinition? Definition { get; set; }

    /// <summary>Saved audit to run; its stored definition/version are used.</summary>
    public Guid? AuditId { get; set; }
}

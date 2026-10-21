namespace PropertyAuditTool.Models;

/// <summary>Saved audit definition. V2 retains legacy scope; V3 adds the
/// table-first multi-culture/all-element view. Execution is validated per run.</summary>
public sealed class AuditDefinition
{
    public int SchemaVersion { get; set; } = 2;

    public bool AllDocumentTypes { get; set; }

    public bool AllElementTypes { get; set; }

    public List<AuditTypeSelection> Types { get; set; } = new();

    public List<AuditColumnDefinition> Columns { get; set; } = new();

    public string? Culture { get; set; }

    /// <summary>V3: all currently permitted cultures, including invariant rows.</summary>
    public bool AllCultures { get; set; }

    /// <summary>V3: explicit culture selection; empty string denotes invariant.
    /// Null retains the legacy single-Culture semantics.</summary>
    public List<string>? Cultures { get; set; }

    public List<AuditColumnDefinition> RowFilters { get; set; } = new();

    /// <summary>Ordered property-column filter chain evaluated left to right;
    /// each link's joiner connects it to the next link. Null derives the chain
    /// from filtered columns in column order with implicit "and" joins —
    /// identical to the pre-chain behaviour.</summary>
    public List<AuditFilterLink>? FilterChain { get; set; }

    public List<string> HiddenBaseColumns { get; set; } = new();
}

/// <summary>One link in the ordered filter chain.</summary>
public sealed class AuditFilterLink
{
    /// <summary>Alias of a filtered column in <see cref="AuditDefinition.Columns"/>.</summary>
    public required string Column { get; set; }

    /// <summary>"and" or "or" — how this link combines with the next link.
    /// Null terminates the chain (last link).</summary>
    public string? Joiner { get; set; }
}

public sealed class AuditTypeSelection
{
    /// <summary>"document" or "element".</summary>
    public required string Kind { get; set; }

    public required string Alias { get; set; }
}

public sealed class AuditColumnDefinition
{
    public required string Alias { get; set; }

    /// <summary>text | number | date | boolean.</summary>
    public required string ValueType { get; set; }

    public AuditColumnFilter? Filter { get; set; }
}

public sealed class AuditColumnFilter
{
    public required string Operator { get; set; }

    public string? Value { get; set; }

    /// <summary>Optional single-comparison expression; when present it is the
    /// authoritative operand and is parsed by <see cref="Services.AuditFilterCompiler"/>.</summary>
    public string? Expression { get; set; }
}

public sealed class AuditWriteRequest
{
    public required string Title { get; set; }

    public string? Description { get; set; }

    public bool IsShared { get; set; }

    public required AuditDefinition Definition { get; set; }

    public int? Version { get; set; }
}

public sealed class AuditResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required bool IsShared { get; init; }

    public required bool IsOwner { get; init; }

    public required string OwnerName { get; init; }

    public required AuditDefinition Definition { get; init; }

    public required int Version { get; init; }

    public required DateTime CreatedDate { get; init; }

    public required DateTime UpdatedDate { get; init; }

    public required AuditDashboardPreferenceResponse Dashboard { get; init; }
}

public sealed class AuditDashboardPreferenceResponse
{
    public required bool IsPinned { get; init; }

    public required string Icon { get; init; }

    public string? Colour { get; init; }

    public long? LastCount { get; init; }

    public DateTime? LastCompletedUtc { get; init; }

    public required bool IsStale { get; init; }
}

/// <summary>The current viewer's personal dashboard preference for an audit.</summary>
public sealed class AuditDashboardPreferenceWriteRequest
{
    public bool IsPinned { get; set; }

    public string? Icon { get; set; }

    public string? Colour { get; set; }
}

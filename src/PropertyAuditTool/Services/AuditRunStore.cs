using System.Collections.Concurrent;
using PropertyAuditTool.Models;

namespace PropertyAuditTool.Services;

/// <summary>
/// Transient viewer-owned audit-run state. Rows live only in memory — no
/// content values are written to the database. Runs expire after
/// <see cref="RunIdleLifetime"/> of inactivity and are pruned on access/create.
/// </summary>
public sealed class AuditRunStore
{
    public static readonly TimeSpan RunIdleLifetime = TimeSpan.FromMinutes(30);
    public const int MaxRunsPerUser = 5;
    public const int MaxRunsTotal = 64;
    public const int MaxWarnings = 100;

    /// <summary>runId -> run state.</summary>
    private readonly ConcurrentDictionary<Guid, AuditRunState> _runs = new();

    /// <summary>Serializes the cap check + add so caps hold under races.</summary>
    private readonly object _createGate = new();

    /// <summary>Retained result cell: raw values are never exposed via DTO.</summary>
    public sealed class RetainedCell
    {
        public required string Alias { get; init; }

        /// <summary>notApplicable | restricted | empty | filled.</summary>
        public required string Status { get; init; }

        /// <summary>Full raw value for filter/sort; never serialized.</summary>
        public object? RawValue { get; init; }

        public string? DisplayValue { get; init; }

        public bool Truncated { get; init; }
    }

    /// <summary>
    /// One step of the block-traversal access path that produced an element
    /// row: the content type and readable block property that contained it.
    /// </summary>
    public sealed record AuditPropertyAccess(Guid TypeKey, string Alias, string EditorAlias);

    public sealed class RetainedRow
    {
        public required string RowKey { get; init; }
        public required string Kind { get; init; }
        public required string TypeAlias { get; init; }
        public required string TypeName { get; init; }
        public required Guid TypeKey { get; init; }
        public required Guid OwnerDocumentKey { get; init; }
        public required string OwnerDocumentName { get; init; }
        public required string Location { get; init; }
        public string? Culture { get; init; }
        public required string EditUrl { get; init; }
        public required List<RetainedCell> Cells { get; init; }

        /// <summary>
        /// Ancestor block-property access path for element rows; empty for
        /// document rows. Revalidated on every return.
        /// </summary>
        public IReadOnlyList<AuditPropertyAccess> TraversalAccess { get; init; } = [];
    }

    public sealed class AuditRunState
    {
        public required Guid RunId { get; init; }
        public required Guid OwnerKey { get; init; }
        public required AuditDefinition Definition { get; init; }
        public Guid? SavedAuditId { get; init; }
        public int? SavedAuditVersion { get; init; }
        public string? NormalizedCulture { get; init; }
        public IReadOnlyList<string?>? NormalizedCultures { get; init; }

        /// <summary>Raw scan cursor over the owner-document candidate set.</summary>
        public long Cursor { get; set; }
        public long RawVisited { get; set; }
        public long ScannedCount { get; set; }

        /// <summary>running | completed | partial | limited | cancelled.</summary>
        public required string Status { get; set; }

        /// <summary>Reader/safety warnings; partial markers.</summary>
        public bool HasPartialWarnings { get; set; }

        /// <summary>A matching row could not be retained under the caps.</summary>
        public bool RetentionExceeded { get; set; }

        /// <summary>Summed raw text length across retained cells.</summary>
        public long RetainedCharacters { get; set; }
        public long RetainedCells { get; set; }

        /// <summary>Completed summary already persisted for this run.</summary>
        public bool SummaryPersisted { get; set; }

        public List<RetainedRow> Rows { get; } = new();
        public List<string> Warnings { get; } = new();

        public required DateTime StartedUtc { get; init; }
        public DateTime? CompletedUtc { get; set; }
        public required DateTime LastTouchedUtc { get; set; }

        /// <summary>Serializes advance/cancel per run.</summary>
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public void AddWarning(string warning)
        {
            if (Warnings.Count < MaxWarnings && !Warnings.Contains(warning))
            {
                Warnings.Add(warning);
            }
        }
    }

    /// <summary>
    /// Returns the run for this viewer or null. Expired runs are pruned.
    /// </summary>
    public AuditRunState? GetForOwner(Guid runId, Guid ownerKey, DateTime utcNow)
    {
        PruneExpired(utcNow);
        return _runs.TryGetValue(runId, out var run) && run.OwnerKey == ownerKey
            ? run
            : null;
    }

    /// <summary>
    /// Registers a new run or fails with TooManyForUser / TooManyTotal.
    /// Active runs are never evicted to make room.
    /// </summary>
    public CreateRunOutcome TryCreate(AuditRunState run, DateTime utcNow)
    {
        PruneExpired(utcNow);
        lock (_createGate)
        {
            if (_runs.Values.Count(existing => existing.OwnerKey == run.OwnerKey) >= MaxRunsPerUser)
            {
                // Completed retained pages are cache entries; release the oldest
                // ones before telling the viewer they have too many runs.
                var finished = _runs.Values
                    .Where(existing => existing.OwnerKey == run.OwnerKey
                        && existing.Status != "running"
                        && existing.Lock.CurrentCount > 0)
                    .OrderBy(existing => existing.LastTouchedUtc);
                foreach (var oldRun in finished)
                {
                    _runs.TryRemove(oldRun.RunId, out _);
                    if (_runs.Values.Count(existing => existing.OwnerKey == run.OwnerKey) < MaxRunsPerUser)
                    {
                        break;
                    }
                }
            }
            if (_runs.Values.Count(existing => existing.OwnerKey == run.OwnerKey) >= MaxRunsPerUser)
            {
                return CreateRunOutcome.TooManyForUser;
            }
            if (_runs.Count >= MaxRunsTotal)
            {
                return CreateRunOutcome.TooManyTotal;
            }
            return _runs.TryAdd(run.RunId, run)
                ? CreateRunOutcome.Created
                : CreateRunOutcome.TooManyTotal;
        }
    }

    public void Remove(Guid runId) => _runs.TryRemove(runId, out _);

    private void PruneExpired(DateTime utcNow)
    {
        foreach (var pair in _runs)
        {
            // Never evict a run currently inside its lock; the owner of the
            // lock will see it on the next touch.
            if (utcNow - pair.Value.LastTouchedUtc > RunIdleLifetime
                && pair.Value.Lock.CurrentCount > 0)
            {
                _runs.TryRemove(pair.Key, out _);
            }
        }
    }

    public enum CreateRunOutcome { Created, TooManyForUser, TooManyTotal }
}

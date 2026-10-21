using System.Security.Cryptography;
using System.Text;
using PropertyAuditTool.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace PropertyAuditTool.Services;

/// <summary>
/// Persistence for saved queries. All SQL uses the host provider's identifier
/// quoting for the fixed table/column names and NPoco parameters for values.
/// </summary>
public class SavedQueryStore
{
    private readonly IScopeProvider _scopeProvider;

    public SavedQueryStore(IScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    private static string T(IScope scope) =>
        scope.SqlContext.SqlSyntax.GetQuotedTableName(SavedQueryRecord.TableName);

    private static string Q(IScope scope, string column) =>
        scope.SqlContext.SqlSyntax.GetQuotedColumnName(column);

    /// <summary>Own records plus every shared record, ordered by name then key.</summary>
    public virtual IReadOnlyList<SavedQueryRecord> ListVisible(Guid currentUserKey)
    {
        using var scope = _scopeProvider.CreateScope();
        var sql =
            $"SELECT * FROM {T(scope)} " +
            $"WHERE ({Q(scope, "ownerKey")} = @0 OR {Q(scope, "isShared")} = @1) " +
            $"ORDER BY {Q(scope, "name")}, {Q(scope, "key")}";
        var records = scope.Database.Fetch<SavedQueryRecord>(sql, currentUserKey, true);
        scope.Complete();
        return records;
    }

    /// <summary>Single record readable by the user (owned or shared); null if invisible.</summary>
    public virtual SavedQueryRecord? GetVisible(Guid id, Guid currentUserKey)
    {
        using var scope = _scopeProvider.CreateScope();
        var sql =
            $"SELECT * FROM {T(scope)} " +
            $"WHERE {Q(scope, "key")} = @0 AND ({Q(scope, "ownerKey")} = @1 OR {Q(scope, "isShared")} = @2)";
        var record = scope.Database.Fetch<SavedQueryRecord>(sql, id, currentUserKey, true)
            .FirstOrDefault();
        scope.Complete();
        return record;
    }

    /// <summary>Single record owned by the user; null if absent or owned by another.</summary>
    public virtual SavedQueryRecord? GetOwned(Guid id, Guid currentUserKey)
    {
        using var scope = _scopeProvider.CreateScope();
        var sql =
            $"SELECT * FROM {T(scope)} " +
            $"WHERE {Q(scope, "key")} = @0 AND {Q(scope, "ownerKey")} = @1";
        var record = scope.Database.Fetch<SavedQueryRecord>(sql, id, currentUserKey)
            .FirstOrDefault();
        scope.Complete();
        return record;
    }

    /// <summary>Creates a new record; all server fields are pre-populated.</summary>
    public virtual void Create(SavedQueryRecord record)
    {
        using var scope = _scopeProvider.CreateScope();
        scope.Database.Insert(SavedQueryRecord.TableName, "key", false, record);
        scope.Complete();
    }

    /// <summary>
    /// Owner-only conditional update. Returns NotFound when no owned record
    /// exists, Conflict when the version race is lost, otherwise Updated.
    /// </summary>
    public virtual UpdateOutcome UpdateOwned(
        Guid id,
        Guid currentUserKey,
        string name,
        bool isShared,
        string serializedDefinition,
        string ownerName,
        DateTime utcNow,
        int expectedVersion)
    {
        using var scope = _scopeProvider.CreateScope();

        var ownedSql =
            $"SELECT * FROM {T(scope)} " +
            $"WHERE {Q(scope, "key")} = @0 AND {Q(scope, "ownerKey")} = @1";
        var owned = scope.Database.Fetch<SavedQueryRecord>(ownedSql, id, currentUserKey)
            .FirstOrDefault();
        if (owned is null)
        {
            return UpdateOutcome.NotFound;
        }

        var updateSql =
            $"UPDATE {T(scope)} " +
            $"SET {Q(scope, "name")} = @0, {Q(scope, "isShared")} = @1, {Q(scope, "definition")} = @2, " +
            $"{Q(scope, "ownerName")} = @3, {Q(scope, "updatedUtc")} = @4, {Q(scope, "version")} = @5 " +
            $"WHERE {Q(scope, "key")} = @6 AND {Q(scope, "ownerKey")} = @7 AND {Q(scope, "version")} = @8";

        var affected = scope.Database.Execute(
            updateSql,
            name, isShared, serializedDefinition, ownerName, utcNow,
            expectedVersion + 1, id, currentUserKey, expectedVersion);

        if (affected == 0)
        {
            return UpdateOutcome.Conflict;
        }

        scope.Complete();

        owned.Name = name;
        owned.IsShared = isShared;
        owned.Definition = serializedDefinition;
        owned.OwnerName = ownerName;
        owned.UpdatedUtc = utcNow;
        owned.Version = expectedVersion + 1;
        return new UpdateOutcome(UpdateStatus.Updated, owned);
    }

    /// <summary>
    /// Owner-only conditional update of a saved audit (v2): writes title,
    /// description, sharing, serialized definition and bumps the version.
    /// </summary>
    public virtual UpdateOutcome UpdateAuditOwned(
        Guid id,
        Guid currentUserKey,
        string title,
        string? description,
        bool isShared,
        string serializedDefinition,
        string ownerName,
        DateTime utcNow,
        int expectedVersion)
    {
        using var scope = _scopeProvider.CreateScope();

        var ownedSql =
            $"SELECT * FROM {T(scope)} " +
            $"WHERE {Q(scope, "key")} = @0 AND {Q(scope, "ownerKey")} = @1";
        var owned = scope.Database.Fetch<SavedQueryRecord>(ownedSql, id, currentUserKey)
            .FirstOrDefault();
        if (owned is null)
        {
            return UpdateOutcome.NotFound;
        }

        var updateSql =
            $"UPDATE {T(scope)} " +
            $"SET {Q(scope, "name")} = @0, {Q(scope, "description")} = @1, {Q(scope, "isShared")} = @2, {Q(scope, "definition")} = @3, " +
            $"{Q(scope, "ownerName")} = @4, {Q(scope, "updatedUtc")} = @5, {Q(scope, "version")} = @6 " +
            $"WHERE {Q(scope, "key")} = @7 AND {Q(scope, "ownerKey")} = @8 AND {Q(scope, "version")} = @9";

        var affected = scope.Database.Execute(
            updateSql,
            title, (object?)description ?? DBNull.Value, isShared, serializedDefinition, ownerName, utcNow,
            expectedVersion + 1, id, currentUserKey, expectedVersion);

        if (affected == 0)
        {
            return UpdateOutcome.Conflict;
        }

        scope.Complete();

        owned.Name = title;
        owned.Description = description;
        owned.IsShared = isShared;
        owned.Definition = serializedDefinition;
        owned.OwnerName = ownerName;
        owned.UpdatedUtc = utcNow;
        owned.Version = expectedVersion + 1;
        return new UpdateOutcome(UpdateStatus.Updated, owned);
    }

    /// <summary>
    /// Owner-only conditional delete. NotFound when no owned record exists,
    /// Conflict on version race, otherwise Deleted.
    /// </summary>
    public virtual DeleteOutcome DeleteOwned(Guid id, Guid currentUserKey, int expectedVersion)
    {
        using var scope = _scopeProvider.CreateScope();

        var ownedSql =
            $"SELECT * FROM {T(scope)} " +
            $"WHERE {Q(scope, "key")} = @0 AND {Q(scope, "ownerKey")} = @1";
        var owned = scope.Database.Fetch<SavedQueryRecord>(ownedSql, id, currentUserKey)
            .FirstOrDefault();
        if (owned is null)
        {
            return DeleteOutcome.NotFound;
        }

        var deleteSql =
            $"DELETE FROM {T(scope)} " +
            $"WHERE {Q(scope, "key")} = @0 AND {Q(scope, "ownerKey")} = @1 AND {Q(scope, "version")} = @2";
        var affected = scope.Database.Execute(deleteSql, id, currentUserKey, expectedVersion);
        if (affected == 0)
        {
            return DeleteOutcome.Conflict;
        }

        scope.Complete();
        return DeleteOutcome.Deleted;
    }

    // ---- dashboard preferences ----------------------------------------------

    /// <summary>
    /// Deterministic preference-row key: first 16 bytes of
    /// SHA-256("userKey:auditKey", UTF-8, lowercase "D" GUID format).
    /// </summary>
    public static Guid DerivePreferenceKey(Guid userKey, Guid auditKey)
    {
        var input = Encoding.UTF8.GetBytes($"{userKey:D}:{auditKey:D}");
        var hash = SHA256.HashData(input);
        var keyBytes = new byte[16];
        Array.Copy(hash, keyBytes, 16);
        return new Guid(keyBytes);
    }

    private static string P(IScope scope) =>
        scope.SqlContext.SqlSyntax.GetQuotedTableName(DashboardPreferenceRecord.TableName);

    /// <summary>All dashboard preferences of the current viewer.</summary>
    public virtual IReadOnlyList<DashboardPreferenceRecord> ListPreferences(Guid currentUserKey)
    {
        using var scope = _scopeProvider.CreateScope();
        var sql = $"SELECT * FROM {P(scope)} WHERE {Q(scope, "userKey")} = @0";
        var records = scope.Database.Fetch<DashboardPreferenceRecord>(sql, currentUserKey);
        scope.Complete();
        return records;
    }

    /// <summary>Single preference by derived key/user/audit; null if absent.</summary>
    public virtual DashboardPreferenceRecord? GetPreference(
        Guid derivedKey, Guid currentUserKey, Guid auditKey)
    {
        using var scope = _scopeProvider.CreateScope();
        var sql =
            $"SELECT * FROM {P(scope)} " +
            $"WHERE {Q(scope, "key")} = @0 AND {Q(scope, "userKey")} = @1 AND {Q(scope, "auditKey")} = @2";
        var record = scope.Database.Fetch<DashboardPreferenceRecord>(sql, derivedKey, currentUserKey, auditKey)
            .FirstOrDefault();
        scope.Complete();
        return record;
    }

    /// <summary>Conditional pin/icon/colour update; false when no row matched.</summary>
    public virtual bool UpdatePreference(
        Guid derivedKey, Guid currentUserKey, Guid auditKey,
        bool isPinned, string icon, string? colour)
    {
        using var scope = _scopeProvider.CreateScope();
        var sql =
            $"UPDATE {P(scope)} SET {Q(scope, "isPinned")} = @0, {Q(scope, "icon")} = @1, {Q(scope, "colour")} = @2 " +
            $"WHERE {Q(scope, "key")} = @3 AND {Q(scope, "userKey")} = @4 AND {Q(scope, "auditKey")} = @5";
        var affected = scope.Database.Execute(
            sql, isPinned, icon, (object?)colour ?? DBNull.Value, derivedKey, currentUserKey, auditKey);
        if (affected > 0)
        {
            scope.Complete();
            return true;
        }
        return false;
    }

    /// <summary>Inserts a fully server-authored preference row.</summary>
    public virtual void InsertPreference(DashboardPreferenceRecord record)
    {
        using var scope = _scopeProvider.CreateScope();
        scope.Database.Insert(DashboardPreferenceRecord.TableName, "key", false, record);
        scope.Complete();
    }

    /// <summary>
    /// Atomic upsert of a viewer's pin/icon/colour: conditional update first;
    /// on absence, insert; on a concurrent insert primary-key conflict, re-read
    /// and apply the same update. Other database errors are not swallowed.
    /// </summary>
    public virtual DashboardPreferenceRecord SetPreference(
        Guid currentUserKey, Guid auditKey, bool isPinned, string icon, string? colour)
    {
        var derivedKey = DerivePreferenceKey(currentUserKey, auditKey);
        if (UpdatePreference(derivedKey, currentUserKey, auditKey, isPinned, icon, colour))
        {
            return GetPreference(derivedKey, currentUserKey, auditKey)!;
        }

        var record = new DashboardPreferenceRecord
        {
            Key = derivedKey,
            UserKey = currentUserKey,
            AuditKey = auditKey,
            IsPinned = isPinned,
            Icon = icon,
            Colour = colour,
        };
        try
        {
            InsertPreference(record);
            return record;
        }
        catch (Exception ex) when (IsUniqueKeyViolation(ex))
        {
            // Concurrent insert of the same derived key won the race.
        }

        if (!UpdatePreference(derivedKey, currentUserKey, auditKey, isPinned, icon, colour))
        {
            throw new InvalidOperationException(
                "Dashboard preference row could not be written after an insert conflict.");
        }
        return GetPreference(derivedKey, currentUserKey, auditKey)
            ?? throw new InvalidOperationException(
                "Dashboard preference row is missing after an insert conflict.");
    }

    /// <summary>Conditional completed-run summary update; false when no row matched.</summary>
    public virtual bool UpdateCompletedSummary(
        Guid derivedKey, Guid currentUserKey, Guid auditKey,
        long completedCount, DateTime utcNow, int runDefinitionVersion)
    {
        using var scope = _scopeProvider.CreateScope();
        var sql =
            $"UPDATE {P(scope)} SET {Q(scope, "lastCount")} = @0, {Q(scope, "lastCompletedUtc")} = @1, {Q(scope, "lastDefinitionVersion")} = @2 " +
            $"WHERE {Q(scope, "key")} = @3 AND {Q(scope, "userKey")} = @4 AND {Q(scope, "auditKey")} = @5";
        var affected = scope.Database.Execute(
            sql, completedCount, utcNow, runDefinitionVersion, derivedKey, currentUserKey, auditKey);
        if (affected > 0)
        {
            scope.Complete();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Advances retained summary versions after metadata-only saved-audit edits.
    /// Rows without a completed-run summary are intentionally left alone.
    /// </summary>
    public virtual void SyncCompletedSummaryDefinitionVersion(Guid auditKey, int definitionVersion)
    {
        using var scope = _scopeProvider.CreateScope();
        var sql =
            $"UPDATE {P(scope)} SET {Q(scope, "lastDefinitionVersion")} = @0 " +
            $"WHERE {Q(scope, "auditKey")} = @1 AND {Q(scope, "lastDefinitionVersion")} IS NOT NULL";
        scope.Database.Execute(sql, definitionVersion, auditKey);
        scope.Complete();
    }

    /// <summary>
    /// Retains a completed-run summary, creating an unpinned default preference
    /// row when none exists. Never stores a partial count.
    /// </summary>
    public virtual DashboardPreferenceRecord SetCompletedSummary(
        Guid currentUserKey, Guid auditKey, long completedCount, DateTime utcNow, int runDefinitionVersion)
    {
        var derivedKey = DerivePreferenceKey(currentUserKey, auditKey);
        if (UpdateCompletedSummary(derivedKey, currentUserKey, auditKey, completedCount, utcNow, runDefinitionVersion))
        {
            return GetPreference(derivedKey, currentUserKey, auditKey)!;
        }

        var record = new DashboardPreferenceRecord
        {
            Key = derivedKey,
            UserKey = currentUserKey,
            AuditKey = auditKey,
            IsPinned = false,
            Icon = "icon-search",
            LastCount = completedCount,
            LastCompletedUtc = utcNow,
            LastDefinitionVersion = runDefinitionVersion,
        };
        try
        {
            InsertPreference(record);
            return record;
        }
        catch (Exception ex) when (IsUniqueKeyViolation(ex))
        {
            // Concurrent insert of the same derived key won the race.
        }

        if (!UpdateCompletedSummary(derivedKey, currentUserKey, auditKey, completedCount, utcNow, runDefinitionVersion))
        {
            throw new InvalidOperationException(
                "Dashboard preference summary row could not be written after an insert conflict.");
        }
        return GetPreference(derivedKey, currentUserKey, auditKey)
            ?? throw new InvalidOperationException(
                "Dashboard preference summary row is missing after an insert conflict.");
    }

    /// <summary>
    /// Provider-neutral unique-key violation check (SQLite extended codes
    /// 1555/2067 or SQL Server 2601/2627 only — other constraint failures such
    /// as NOT NULL or CHECK must not be swallowed).
    /// </summary>
    public static bool IsUniqueKeyViolation(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException!)
        {
            var type = e.GetType();
            if (type.FullName == "Microsoft.Data.Sqlite.SqliteException")
            {
                // SqliteExtendedErrorCode disambiguates SQLITE_CONSTRAINT (19):
                // 1555 = primary key, 2067 = unique. Fall back to the base code
                // only when it already carries the extended value.
                var extended = type.GetProperty("SqliteExtendedErrorCode")?.GetValue(e) as int?;
                var code = type.GetProperty("SqliteErrorCode")?.GetValue(e) as int?;
                if ((extended ?? code) is 1555 or 2067)
                {
                    return true;
                }
            }
            else if (type.FullName is "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException")
            {
                if (type.GetProperty("Number")?.GetValue(e) is int sqlNumber
                    && sqlNumber is 2601 or 2627)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public sealed record UpdateOutcome(UpdateStatus Status, SavedQueryRecord? Record = null)
    {
        public static readonly UpdateOutcome NotFound = new(UpdateStatus.NotFound);
        public static readonly UpdateOutcome Conflict = new(UpdateStatus.Conflict);
    }

    public sealed record DeleteOutcome(DeleteStatus Status)
    {
        public static readonly DeleteOutcome NotFound = new(DeleteStatus.NotFound);
        public static readonly DeleteOutcome Conflict = new(DeleteStatus.Conflict);
        public static readonly DeleteOutcome Deleted = new(DeleteStatus.Deleted);
    }

    public enum UpdateStatus { Updated, NotFound, Conflict }

    public enum DeleteStatus { Deleted, NotFound, Conflict }
}

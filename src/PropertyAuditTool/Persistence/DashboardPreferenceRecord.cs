using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace PropertyAuditTool.Persistence;

/// <summary>
/// NPoco runtime record for the <c>propertyAuditDashboardPreference</c> table:
/// one row per (user, audit) holding personal pin/icon/colour plus the last
/// completed-run summary. Column names are fixed by the additive migration.
/// </summary>
[TableName(TableName)]
[PrimaryKey("key", AutoIncrement = false)]
[ExplicitColumns]
public class DashboardPreferenceRecord
{
    public const string TableName = "propertyAuditDashboardPreference";

    /// <summary>Deterministic SHA-256-derived key; one row per viewer/audit.</summary>
    [Column("key")]
    [PrimaryKeyColumn(AutoIncrement = false, Name = "PK_propertyAuditDashboardPreference")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public Guid Key { get; set; }

    [Column("userKey")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public Guid UserKey { get; set; }

    [Column("auditKey")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public Guid AuditKey { get; set; }

    [Column("isPinned")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public bool IsPinned { get; set; }

    [Column("icon")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    [Length(64)]
    public required string Icon { get; set; }

    [Column("colour")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Length(7)]
    public string? Colour { get; set; }

    [Column("lastCount")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public long? LastCount { get; set; }

    [Column("lastCompletedUtc")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public DateTime? LastCompletedUtc { get; set; }

    [Column("lastDefinitionVersion")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public int? LastDefinitionVersion { get; set; }
}

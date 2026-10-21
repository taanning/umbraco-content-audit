using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace PropertyAuditTool.Migrations;

/// <summary>
/// Schema snapshot used only by the additive table-creation migration.
/// Kept separate from the runtime <see cref="Persistence.SavedQueryRecord"/>.
/// </summary>
[TableName(TableName)]
[PrimaryKey("key", AutoIncrement = false)]
[ExplicitColumns]
public class SavedQueryTableSchema
{
    public const string TableName = "propertyAuditSavedQuery";

    [Column("key")]
    [PrimaryKeyColumn(AutoIncrement = false, Name = "PK_propertyAuditSavedQuery")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public Guid Key { get; set; }

    [Column("ownerKey")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    [Index(IndexTypes.NonClustered, Name = "IX_propertyAuditSavedQuery_ownerKey")]
    public Guid OwnerKey { get; set; }

    [Column("name")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    [Length(120)]
    public required string Name { get; set; }

    [Column("ownerName")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    [Length(255)]
    public required string OwnerName { get; set; }

    [Column("isShared")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    [Index(IndexTypes.NonClustered, Name = "IX_propertyAuditSavedQuery_isShared")]
    public bool IsShared { get; set; }

    [Column("definition")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    [SpecialDbType(SpecialDbTypes.NVARCHARMAX)]
    public required string Definition { get; set; }

    [Column("createdUtc")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public DateTime CreatedUtc { get; set; }

    [Column("updatedUtc")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public DateTime UpdatedUtc { get; set; }

    [Column("version")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public int Version { get; set; }
}

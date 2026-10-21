using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace PropertyAuditTool.Migrations;

/// <summary>
/// Schema snapshot for the v2 additive change: a nullable <c>description</c>
/// column on <c>propertyAuditSavedQuery</c>. The shipped v1 snapshot
/// <see cref="SavedQueryTableSchema"/> is never modified.
/// </summary>
[TableName(SavedQueryTableSchema.TableName)]
[PrimaryKey("key", AutoIncrement = false)]
[ExplicitColumns]
public class SavedQueryTableSchemaV2
{
    public const string TableName = SavedQueryTableSchema.TableName;

    [Column("key")]
    [PrimaryKeyColumn(AutoIncrement = false, Name = "PK_propertyAuditSavedQuery")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
    public Guid Key { get; set; }

    [Column("ownerKey")]
    [NullSetting(NullSetting = NullSettings.NotNull)]
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
    public bool IsShared { get; set; }

    [Column("description")]
    [NullSetting(NullSetting = NullSettings.Null)]
    [Length(2000)]
    public string? Description { get; set; }

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

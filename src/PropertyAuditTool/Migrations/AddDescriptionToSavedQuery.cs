using Umbraco.Cms.Infrastructure.Migrations;

namespace PropertyAuditTool.Migrations;

/// <summary>
/// Adds the nullable <c>description</c> column to
/// <c>propertyAuditSavedQuery</c>. Additive only; existing rows keep NULL.
/// </summary>
public class AddDescriptionToSavedQuery : AsyncMigrationBase
{
    public AddDescriptionToSavedQuery(IMigrationContext context)
        : base(context)
    {
    }

    protected override Task MigrateAsync()
    {
        if (TableExists(SavedQueryTableSchema.TableName)
            && !ColumnExists(SavedQueryTableSchema.TableName, "description"))
        {
            // Portable DDL: SQL Server rejects the SQLite-only COLUMN keyword;
            // both accept ADD without it. NPoco's fluent Alter.Table is
            // unsupported on SQLite.
            var table = SqlSyntax.GetQuotedTableName(SavedQueryTableSchema.TableName);
            var column = SqlSyntax.GetQuotedColumnName("description");
            Database.Execute($"ALTER TABLE {table} ADD {column} NVARCHAR(2000) NULL");
        }

        return Task.CompletedTask;
    }
}

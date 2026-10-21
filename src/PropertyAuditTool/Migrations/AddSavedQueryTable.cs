using Umbraco.Cms.Infrastructure.Migrations;

namespace PropertyAuditTool.Migrations;

/// <summary>
/// Creates the <c>propertyAuditSavedQuery</c> table if it does not exist.
/// Additive only: never drops, replaces, or alters any existing table.
/// </summary>
public class AddSavedQueryTable : AsyncMigrationBase
{
    public AddSavedQueryTable(IMigrationContext context)
        : base(context)
    {
    }

    protected override Task MigrateAsync()
    {
        if (!TableExists(SavedQueryTableSchema.TableName))
        {
            Create.Table<SavedQueryTableSchema>().Do();
        }

        return Task.CompletedTask;
    }
}

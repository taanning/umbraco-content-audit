using Umbraco.Cms.Infrastructure.Migrations;

namespace PropertyAuditTool.Migrations;

/// <summary>
/// Creates the <c>propertyAuditDashboardPreference</c> table if it does not
/// exist. Additive only: no CMS table foreign keys, drops, or truncations.
/// </summary>
public class AddDashboardPreferenceTable : AsyncMigrationBase
{
    public AddDashboardPreferenceTable(IMigrationContext context)
        : base(context)
    {
    }

    protected override Task MigrateAsync()
    {
        if (!TableExists(DashboardPreferenceTableSchema.TableName))
        {
            Create.Table<DashboardPreferenceTableSchema>().Do();
        }

        return Task.CompletedTask;
    }
}

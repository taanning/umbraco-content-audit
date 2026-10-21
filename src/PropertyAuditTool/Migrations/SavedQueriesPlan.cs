using Umbraco.Cms.Infrastructure.Migrations;

namespace PropertyAuditTool.Migrations;

/// <summary>Migration plan for the additive saved-queries table.</summary>
public class SavedQueriesPlan : MigrationPlan
{
    public const string PlanName = "PropertyAuditTool.SavedQueries";
    public const string SavedQueriesV1 = "saved-queries-v1";
    public const string AuditDashboardV2 = "audit-dashboard-v2";
    public const string AuditDashboardV2Preferences = "audit-dashboard-v2-preferences";

    public SavedQueriesPlan()
        : base(PlanName)
    {
        From(string.Empty).To<AddSavedQueryTable>(SavedQueriesV1);
        From(SavedQueriesV1).To<AddDescriptionToSavedQuery>(AuditDashboardV2);
        From(AuditDashboardV2).To<AddDashboardPreferenceTable>(AuditDashboardV2Preferences);
    }
}

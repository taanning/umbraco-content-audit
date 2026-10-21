using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Upgrade;

namespace PropertyAuditTool.Migrations;

/// <summary>
/// Runs the saved-queries migration plan once when the CMS is at
/// <see cref="RuntimeLevel.Run"/>. Uses <see cref="ICoreScopeProvider"/>,
/// which exists on Umbraco 17 and 18.
/// </summary>
public class SavedQueriesMigrationRunner : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private readonly IMigrationPlanExecutor _migrationPlanExecutor;
    private readonly ICoreScopeProvider _scopeProvider;
    private readonly IKeyValueService _keyValueService;
    private readonly IRuntimeState _runtimeState;

    public SavedQueriesMigrationRunner(
        IMigrationPlanExecutor migrationPlanExecutor,
        ICoreScopeProvider scopeProvider,
        IKeyValueService keyValueService,
        IRuntimeState runtimeState)
    {
        _migrationPlanExecutor = migrationPlanExecutor;
        _scopeProvider = scopeProvider;
        _keyValueService = keyValueService;
        _runtimeState = runtimeState;
    }

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (_runtimeState.Level != RuntimeLevel.Run)
        {
            return;
        }

        var plan = new SavedQueriesPlan();
        var upgrader = new Upgrader(plan);
        var executed = await upgrader.ExecuteAsync(
            _migrationPlanExecutor, _scopeProvider, _keyValueService);
        if (!executed.Successful)
        {
            throw new InvalidOperationException(
                $"Migration plan '{plan.GetType().Name}' failed at state " +
                $"'{executed.InitialState} -> {executed.FinalState}'.",
                executed.Exception);
        }
    }
}

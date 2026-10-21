using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using PropertyAuditTool.Migrations;
using PropertyAuditTool.Services;

namespace PropertyAuditTool.Composers
{
    /// <summary>
    /// Registers package services and the additive saved-queries migration.
    /// The API rides the built-in Management OpenAPI document; no custom
    /// Swagger/OpenAPI handlers are registered because those types were
    /// removed in Umbraco 18.
    /// </summary>
    public class PropertyAuditToolApiComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddScoped<PropertyAuditService>();
            builder.Services.AddScoped<SavedQueryStore>();
            builder.Services.AddScoped<SavedQueryService>();
            builder.Services.AddScoped<SavedAuditService>();
            builder.Services.AddSingleton<AuditRunStore>();
            builder.Services.AddScoped<AuditExecutionService>();
            builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, SavedQueriesMigrationRunner>();
        }
    }
}

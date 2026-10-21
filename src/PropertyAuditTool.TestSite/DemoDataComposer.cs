using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace PropertyAuditTool.TestSite;

/// <summary>
/// Test-site-only demo fixture. Seeds one "Audit Demo" document type and two
/// root documents (one empty, one filled `description`) so the Property Audit
/// dashboard can be exercised locally. Runs only in Development when
/// <c>Umbraco:CMS:PropertyAuditTool:SeedDemoContent</c> is enabled and only
/// when the demo type does not already exist. Nothing here ships in the package.
/// </summary>
public class DemoDataComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoDataSeeder>();
    }
}

public class DemoDataSeeder : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string DocTypeAlias = "auditDemo";
    private const string PropertyAlias = "description";

    private readonly IShortStringHelper _shortStringHelper;
    private readonly IDataTypeService _dataTypeService;
    private readonly IContentTypeService _contentTypeService;
    private readonly IContentService _contentService;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IConfiguration _configuration;

    public DemoDataSeeder(
        IShortStringHelper shortStringHelper,
        IDataTypeService dataTypeService,
        IContentTypeService contentTypeService,
        IContentService contentService,
        IHostEnvironment hostEnvironment,
        IConfiguration configuration)
    {
        _shortStringHelper = shortStringHelper;
        _dataTypeService = dataTypeService;
        _contentTypeService = contentTypeService;
        _contentService = contentService;
        _hostEnvironment = hostEnvironment;
        _configuration = configuration;
    }

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (!_hostEnvironment.IsDevelopment()
            || !_configuration.GetValue<bool>("Umbraco:CMS:PropertyAuditTool:SeedDemoContent"))
        {
            return;
        }

        var documentType = _contentTypeService.Get(DocTypeAlias);
        if (documentType is null)
        {
            var dataType = (await _dataTypeService.GetByEditorAliasAsync("Umbraco.TextBox")).FirstOrDefault()
                ?? throw new InvalidOperationException("A Textstring data type was not found.");

            documentType = new ContentType(_shortStringHelper, -1)
            {
                Alias = DocTypeAlias,
                Name = "Audit Demo",
                Icon = "icon-document",
                AllowedAsRoot = true,
            };
            documentType.AddPropertyType(
                new PropertyType(_shortStringHelper, dataType)
                {
                    Alias = PropertyAlias,
                    Name = "Description",
                },
                "content",
                "Content");
            await _contentTypeService.CreateAsync(documentType, Umbraco.Cms.Core.Constants.Security.SuperUserKey);
        }

        var alreadySeeded = _contentService.GetPagedOfTypes(
                new[] { documentType.Id },
                0,
                1,
                out _,
                filter: null,
                ordering: Ordering.By("Id"))
            .Any();
        if (alreadySeeded)
        {
            return;
        }

        var empty = new Content("Empty description demo", -1, documentType);
        empty.SetValue(PropertyAlias, string.Empty);
        _contentService.Save(empty);

        var filled = new Content("Filled description demo", -1, documentType);
        filled.SetValue(PropertyAlias, "Some demo content text");
        _contentService.Save(filled);
    }
}

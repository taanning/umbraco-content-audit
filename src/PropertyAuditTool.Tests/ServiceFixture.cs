using Moq;
using PropertyAuditTool.Models;
using PropertyAuditTool.Services;
using Umbraco.Cms.Core.Hosting;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Core.Services;

namespace PropertyAuditTool.Tests;

/// <summary>
/// Wires <see cref="PropertyAuditService"/> to mocked Umbraco services.
/// </summary>
internal sealed class ServiceFixture
{
    public Mock<IContentTypeService> ContentTypeService { get; } = new();
    public Mock<IContentService> ContentService { get; } = new();
    public Mock<ILanguageService> LanguageService { get; } = new();
    public Mock<IContentPermissionAuthorizer> Authorizer { get; } = new();
    public Mock<IHostingEnvironment> HostingEnvironment { get; } = new();

    /// <summary>Raw node list paged by the fake content service (ordered by id).</summary>
    public List<IContent> Nodes { get; } = new();

    /// <summary>Keys the mock authorizer treats as Browse-authorized; null means "all".</summary>
    public Func<IContent, bool>? AuthorizationFilter { get; set; }

    /// <summary>ISO codes the mock authorizer denies for the user.</summary>
    public HashSet<string> DeniedCultures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IContentType> ContentTypes
    {
        set => ContentTypeService.Setup(s => s.GetAll()).Returns(value);
    }

    public IReadOnlyList<ILanguage> Languages
    {
        set => LanguageService.Setup(s => s.GetAllAsync()).ReturnsAsync(value);
    }

    public PropertyAuditService Create()
    {
        ContentService
            .Setup(s => s.GetPagedOfTypes(
                It.IsAny<int[]>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                out It.Ref<long>.IsAny,
                It.IsAny<IQuery<IContent>?>(),
                It.IsAny<Ordering?>()))
            .Returns(new GetPagedDelegate(GetPaged));

        // Compatible-authorizer contract: the batch call answers whether ANY
        // key in the batch is denied (missing node or failing filter).
        Authorizer
            .Setup(s => s.IsDeniedAsync(
                It.IsAny<IUser>(),
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<ISet<string>>()))
            .ReturnsAsync((IUser _, IEnumerable<Guid> keys, ISet<string> _) =>
                keys.Any(key =>
                {
                    var node = Nodes.FirstOrDefault(n => n.Key == key);
                    return node is null || !(AuthorizationFilter?.Invoke(node) ?? true);
                }));

        Authorizer
            .Setup(s => s.IsDeniedForCultures(It.IsAny<IUser>(), It.IsAny<ISet<string>>()))
            .ReturnsAsync((IUser _, ISet<string> cultures) => cultures.Any(DeniedCultures.Contains));

        HostingEnvironment
            .Setup(h => h.ToAbsolute(It.IsAny<string>()))
            .Returns((string path) => path.Replace("~", string.Empty));

        return new PropertyAuditService(
            ContentTypeService.Object,
            ContentService.Object,
            LanguageService.Object,
            Authorizer.Object,
            HostingEnvironment.Object);
    }

    private IEnumerable<IContent> GetPaged(
        int[] contentTypeIds,
        long pageIndex,
        int pageSize,
        out long totalRecords,
        IQuery<IContent>? filter,
        Ordering? ordering)
    {
        var matching = Nodes.Where(n => contentTypeIds.Contains(n.ContentType.Id)).ToList();
        totalRecords = matching.Count;
        return matching.Skip((int)(pageIndex * pageSize)).Take(pageSize).ToArray();
    }

    private delegate IEnumerable<IContent> GetPagedDelegate(
        int[] contentTypeIds,
        long pageIndex,
        int pageSize,
        out long totalRecords,
        IQuery<IContent>? filter,
        Ordering? ordering);
}

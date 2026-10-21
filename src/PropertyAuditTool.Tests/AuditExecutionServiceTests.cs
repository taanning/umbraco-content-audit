using Moq;
using PropertyAuditTool.Models;
using PropertyAuditTool.Persistence;
using PropertyAuditTool.Services;
using Umbraco.Cms.Core.Hosting;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Xunit;

namespace PropertyAuditTool.Tests;

/// <summary>
/// Incremental run engine tests over mocked Umbraco services. The fake content
/// service pages the full matching candidate set by id in pages of
/// <see cref="AuditExecutionService.PageSize"/> and reports the full count in
/// the out parameter — not the page count.
/// </summary>
public class AuditExecutionServiceTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Camel =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private static readonly Guid UserAKey = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserBKey = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed class RunFixture
    {
        public Mock<IContentTypeService> ContentTypeService { get; } = new();
        public Mock<IContentService> ContentService { get; } = new();
        public Mock<ILanguageService> LanguageService { get; } = new();
        public Mock<IContentPermissionAuthorizer> Authorizer { get; } = new();
        public Mock<IHostingEnvironment> HostingEnvironment { get; } = new();
        public Mock<SavedQueryStore> SavedStore { get; } = new(Mock.Of<IScopeProvider>());
        public AuditRunStore Store { get; } = new();
        public List<IContent> Nodes { get; } = new();

        /// <summary>Per-viewer Browse filter; null = everyone allowed.</summary>
        public Func<IUser, IContent, bool>? AuthorizationFilter { get; set; }
        public HashSet<string> DeniedCultures { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, SavedQueryRecord> VisibleRecords { get; } = new();
        public List<(Guid User, Guid Audit, long Count, int Version)> SummaryCalls { get; } = new();

        public IReadOnlyList<IContentType> ContentTypes
        {
            set => ContentTypeService.Setup(s => s.GetAll()).Returns(value);
        }

        public IReadOnlyList<ILanguage> Languages
        {
            set => LanguageService.Setup(s => s.GetAllAsync()).ReturnsAsync(value);
        }

        public AuditExecutionService Create()
        {
            ContentService
                .Setup(s => s.GetPagedOfTypes(
                    It.IsAny<int[]>(), It.IsAny<long>(), It.IsAny<int>(),
                    out It.Ref<long>.IsAny,
                    It.IsAny<IQuery<IContent>?>(), It.IsAny<Ordering?>()))
                .Returns(new GetPagedDelegate(GetPaged));

            Authorizer
                .Setup(s => s.IsDeniedAsync(
                    It.IsAny<IUser>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<ISet<string>>()))
                .ReturnsAsync((IUser user, IEnumerable<Guid> keys, ISet<string> _) =>
                    keys.Any(key =>
                    {
                        var node = Nodes.FirstOrDefault(n => n.Key == key);
                        return node is null
                            || !(AuthorizationFilter?.Invoke(user, node) ?? true);
                    }));

            Authorizer
                .Setup(s => s.IsDeniedForCultures(It.IsAny<IUser>(), It.IsAny<ISet<string>>()))
                .ReturnsAsync((IUser _, ISet<string> cultures) =>
                    cultures.Any(DeniedCultures.Contains));

            HostingEnvironment
                .Setup(h => h.ToAbsolute(It.IsAny<string>()))
                .Returns((string path) => path.Replace("~", string.Empty));

            SavedStore
                .Setup(s => s.GetVisible(It.IsAny<Guid>(), It.IsAny<Guid>()))
                .Returns((Guid id, Guid viewer) =>
                    VisibleRecords.TryGetValue(id, out var record)
                    && (record.IsShared || record.OwnerKey == viewer)
                        ? record
                        : null);

            SavedStore
                .Setup(s => s.SetCompletedSummary(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<long>(),
                    It.IsAny<DateTime>(), It.IsAny<int>()))
                .Callback((Guid user, Guid audit, long count, DateTime _, int version) =>
                    SummaryCalls.Add((user, audit, count, version)))
                .Returns(new DashboardPreferenceRecord
                {
                    Key = Guid.NewGuid(), Icon = "icon-search",
                });

            return new AuditExecutionService(
                Store,
                ContentService.Object,
                ContentTypeService.Object,
                LanguageService.Object,
                Authorizer.Object,
                HostingEnvironment.Object,
                SavedStore.Object);
        }

        private IEnumerable<IContent> GetPaged(
            int[] contentTypeIds, long pageIndex, int pageSize,
            out long totalRecords, IQuery<IContent>? filter, Ordering? ordering)
        {
            var matching = Nodes
                .Where(n => contentTypeIds.Contains(n.ContentType.Id))
                .OrderBy(n => n.Id)
                .ToList();
            totalRecords = matching.Count; // full matching count, not page count
            return matching.Skip((int)(pageIndex * pageSize)).Take(pageSize).ToArray();
        }

        private delegate IEnumerable<IContent> GetPagedDelegate(
            int[] contentTypeIds, long pageIndex, int pageSize,
            out long totalRecords, IQuery<IContent>? filter, Ordering? ordering);
    }

    private static readonly Guid TitleKey = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid AuthorKey = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid RatingKey = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid HiddenKey = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid BlocksKey = Guid.Parse("cccccccc-0000-0000-0000-000000000005");
    private static readonly Guid ElTitleKey = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private static IPropertyType TextProp(
        string alias, Guid? key = null,
        ContentVariation variations = ContentVariation.Nothing,
        string editorAlias = "Umbraco.TextBox") =>
        TestBuilders.PropertyType(alias, key, variations, editorAlias: editorAlias).Object;

    private static IContentType DocType(
        string alias, int id, ContentVariation variations = ContentVariation.Nothing,
        params IPropertyType[] compositionProperties)
    {
        var mock = TestBuilders.ContentType(alias, id, variations: variations,
            compositionProperties: compositionProperties);
        mock.SetupGet(t => t.Key).Returns(GuidForType(alias));
        return mock.Object;
    }

    private static IContentType ElementType(
        string alias, int id, params IPropertyType[] compositionProperties)
    {
        var mock = TestBuilders.ContentType(alias, id, isElement: true,
            compositionProperties: compositionProperties);
        mock.SetupGet(t => t.Key).Returns(GuidForType(alias));
        return mock.Object;
    }

    private static readonly Dictionary<string, Guid> TypeKeys = new(StringComparer.Ordinal);
    private static Guid GuidForType(string alias)
    {
        if (!TypeKeys.TryGetValue(alias, out var key))
        {
            key = Guid.NewGuid();
            TypeKeys[alias] = key;
        }
        return key;
    }

    private static IUser User(Guid key, bool readable = true, string? deniedPropertyKey = null)
    {
        var group = deniedPropertyKey is null
            ? TestBuilders.UserGroup(permissions:
                readable ? new[] { "Umb.Document.PropertyValue.Read" } : Array.Empty<string>())
            : TestBuilders.UserGroup(
                permissions: new[] { "Umb.Document.PropertyValue.Read" },
                granularPermissions: new[]
                {
                    TestBuilders.PropertyValueGranular(Guid.Empty, $"{deniedPropertyKey}|"),
                });
        var mock = new Mock<IUser>();
        mock.SetupGet(u => u.Key).Returns(key);
        mock.SetupGet(u => u.Groups).Returns(new[] { group });
        return mock.Object;
    }

    private static IUser UserA() => User(UserAKey);
    private static IUser UserB() => User(UserBKey);

    private static Mock<IContent> Doc(
        int id, IContentType type, string name,
        IDictionary<string, object?>? values = null,
        bool trashed = false)
    {
        var mock = new Mock<IContent>();
        mock.SetupGet(c => c.Id).Returns(id);
        mock.SetupGet(c => c.Key).Returns(Guid.NewGuid());
        mock.SetupGet(c => c.Trashed).Returns(trashed);
        mock.SetupGet(c => c.Name).Returns(name);
        mock.SetupGet(c => c.ContentType)
            .Returns(TestBuilders.SimpleContentType(type.Alias, type.Id,
                (ContentVariation)type.Variations).Object);
        mock.Setup(c => c.GetValue(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), false))
            .Returns((string alias, string? _, string? _, bool _) =>
                values is not null && values.TryGetValue(alias, out var v) ? v : null);
        mock.Setup(c => c.GetCultureName(It.IsAny<string>()))
            .Returns((string culture) => culture == "en-US" ? name : null);
        return mock;
    }

    private static AuditDefinition Definition(
        IReadOnlyList<AuditTypeSelection>? types = null,
        IReadOnlyList<AuditColumnDefinition>? columns = null,
        string? culture = null,
        bool allDocumentTypes = false) =>
        new()
        {
            SchemaVersion = 2,
            AllDocumentTypes = allDocumentTypes,
            Types = (types ?? new List<AuditTypeSelection>
            {
                new() { Kind = "document", Alias = "article" },
            }).ToList(),
            Columns = (columns ?? new List<AuditColumnDefinition>
            {
                new() { Alias = "title", ValueType = "text" },
            }).ToList(),
            Culture = culture,
        };

    private static AuditRunCreateRequest Draft(AuditDefinition definition) =>
        new() { Definition = definition };

    private static AuditColumnDefinition Column(
        string alias, string valueType = "text", AuditColumnFilter? filter = null) =>
        new() { Alias = alias, ValueType = valueType, Filter = filter };

    [Fact]
    public async Task Table_AllCultures_ProducesVariantRowsAndInvariantOnlyOnce()
    {
        var fixture = new RunFixture();
        var variant = DocType("article", 1, ContentVariation.Culture,
            TextProp("title", variations: ContentVariation.Culture));
        var invariant = DocType("plain", 2, compositionProperties: TextProp("title"));
        fixture.ContentTypes = [variant, invariant];
        fixture.Languages = [TestBuilders.Language("en-US"), TestBuilders.Language("da-DK")];
        var node = Doc(1, variant, "Variant", new Dictionary<string, object?> { ["title"] = "text" });
        node.Setup(content => content.GetCultureName(It.IsAny<string>())).Returns((string culture) => $"Page {culture}");
        fixture.Nodes.AddRange([node.Object, Doc(2, invariant, "Invariant").Object]);
        var definition = Definition(allDocumentTypes: true);
        definition.SchemaVersion = 3;
        definition.AllCultures = true;
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var completed = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal("completed", completed.Value!.Status);
        Assert.Equal(3, completed.Value.TotalCount);
        var results = await service.GetResultsAsync(UserA(), created.Value.RunId, 0, 50, "culture", "asc", CancellationToken.None);
        Assert.Equal(3, results.Value!.Items.Select(row => row.RowKey).Distinct().Count());
        Assert.Equal(new string?[] { null, "da-DK", "en-US" }, results.Value.Items.Select(row => row.Culture));
    }

    [Fact]
    public async Task Table_AllCultures_ExcludesDeniedCulture_AndRechecksRevocation()
    {
        var fixture = new RunFixture();
        var variant = DocType("article", 1, ContentVariation.Culture,
            TextProp("title", variations: ContentVariation.Culture));
        fixture.ContentTypes = [variant];
        fixture.Languages = [TestBuilders.Language("en-US"), TestBuilders.Language("da-DK")];
        var node = Doc(1, variant, "Variant");
        node.Setup(content => content.GetCultureName(It.IsAny<string>())).Returns("Variant");
        fixture.Nodes.Add(node.Object);
        fixture.DeniedCultures.Add("da-DK");
        var definition = Definition();
        definition.SchemaVersion = 3;
        definition.AllCultures = true;
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var completed = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal(1, completed.Value!.TotalCount);
        fixture.DeniedCultures.Add("en-US");
        var denied = await service.GetResultsAsync(UserA(), created.Value.RunId, 0, 50, null, null, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Forbidden, denied.Status);
    }

    [Fact]
    public async Task Table_EmptyTypeSelection_CompletesWithZero_NotAllDocuments()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = [type];
        fixture.Languages = [TestBuilders.Language("en-US")];
        fixture.Nodes.Add(Doc(1, type, "Existing").Object);
        var definition = Definition(types: []);
        definition.SchemaVersion = 3;
        definition.Columns.Clear();
        definition.Cultures = [""];
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var completed = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal("completed", completed.Value!.Status);
        Assert.Equal(0, completed.Value.TotalCount);
    }

    [Fact]
    public async Task Table_BaseColumnFilter_IsAppliedBeforeRetention()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = [type];
        fixture.Languages = [TestBuilders.Language("en-US")];
        fixture.Nodes.AddRange([Doc(1, type, "Keep this").Object, Doc(2, type, "Exclude this").Object]);
        var definition = Definition();
        definition.SchemaVersion = 3;
        definition.Cultures = [""];
        definition.RowFilters = [Column("content", filter: new() { Operator = "contains", Value = "Keep" })];
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var completed = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal(1, completed.Value!.TotalCount);
    }

    [Fact]
    public async Task Table_AllElements_FindsBlocksWithoutExplicitTypeList()
    {
        var fixture = new RunFixture();
        var owner = DocType("article", 1, compositionProperties: TextProp("blocks", editorAlias: "Umbraco.BlockList"));
        var element = ElementType("link", 9, TextProp("title"));
        fixture.ContentTypes = [owner, element];
        fixture.Languages = [TestBuilders.Language("en-US"), TestBuilders.Language("da-DK")];
        fixture.Nodes.Add(DocWithBlocks(owner, Guid.NewGuid(), "link").Object);
        var definition = Definition(types: []);
        definition.SchemaVersion = 3;
        definition.AllElementTypes = true;
        definition.AllCultures = true;
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var completed = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal(1, completed.Value!.TotalCount);
    }

    [Fact]
    public async Task Table_BlockExposure_DoesNotInventOtherCultureRows()
    {
        var fixture = new RunFixture();
        var owner = DocType("article", 1, compositionProperties: TextProp("blocks", editorAlias: "Umbraco.BlockList"));
        var element = ElementType("link", 9, TextProp("title"));
        fixture.ContentTypes = [owner, element];
        fixture.Languages = [TestBuilders.Language("en-US"), TestBuilders.Language("da-DK")];
        var key = Guid.NewGuid();
        var node = DocWithBlocks(owner, key, "link");
        var payload = System.Text.Json.Nodes.JsonNode.Parse(
            node.Object.GetValue("blocks", null, null, false)!.ToString()!)!;
        payload["expose"] = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject { ["contentKey"] = key.ToString(), ["culture"] = "en-US" },
        };
        node.Setup(content => content.GetValue("blocks", It.IsAny<string?>(), It.IsAny<string?>(), false))
            .Returns(payload.ToJsonString());
        fixture.Nodes.Add(node.Object);
        var definition = Definition(types: []);
        definition.SchemaVersion = 3;
        definition.AllElementTypes = true;
        definition.AllCultures = true;
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        var results = await service.GetResultsAsync(UserA(), created.Value.RunId, 0, 50, null, null, CancellationToken.None);
        Assert.Equal("en-US", Assert.Single(results.Value!.Items).Culture);
    }

    [Fact]
    public async Task Table_RetainedCellBudget_IsLimited_NotAnExactTotal()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = [type];
        fixture.Languages = [TestBuilders.Language("en-US")];
        fixture.Nodes.Add(Doc(1, type, "A").Object);
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        fixture.Store.GetForOwner(created.Value!.RunId, UserAKey, DateTime.UtcNow)!.RetainedCells =
            AuditExecutionService.MaxRetainedCells;
        var progress = await service.AdvanceAsync(UserA(), created.Value.RunId, CancellationToken.None);
        Assert.Equal("limited", progress.Value!.Status);
        Assert.Null(progress.Value.TotalCount);
    }

    // ---- lifecycle ------------------------------------------------------------

    [Fact]
    public async Task Advance_ExactCandidates_CompletedWithExactTotal()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.AddRange(new[]
        {
            Doc(1, type, "A", new Dictionary<string, object?> { ["title"] = "one" }).Object,
            Doc(2, type, "B", new Dictionary<string, object?> { ["title"] = "two" }).Object,
        });
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal("completed", advance.Value!.Status);
        Assert.Equal(2, advance.Value.TotalCount);
        Assert.Equal(2, advance.Value.ScannedCount);
    }

    [Fact]
    public async Task Advance_MoreThan2000Raw_RequiresSecondAdvance_NoDoubleCount()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        for (var i = 0; i < 2100; i++)
        {
            fixture.Nodes.Add(Doc(i + 1, type, $"n{i}",
                new Dictionary<string, object?> { ["title"] = $"t{i}" }).Object);
        }
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);

        var first = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal("running", first.Value!.Status);
        Assert.Equal(2000, first.Value.ScannedCount);

        var second = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal("completed", second.Value!.Status);
        Assert.Equal(2100, second.Value.ScannedCount);
        Assert.Equal(2100, second.Value.TotalCount);

        // Advancing a finished run must not double count.
        var third = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal(2100, third.Value!.TotalCount);
    }

    [Fact]
    public async Task MultipleTypes_UnionColumns_MissingAliasNotApplicable()
    {
        var fixture = new RunFixture();
        var title = TextProp("title");
        var author = TextProp("author");
        var articleType = DocType("article", 1, compositionProperties: title);
        var newsType = DocType("news", 2, compositionProperties: author);
        fixture.ContentTypes = new IContentType[] { articleType, newsType };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, articleType, "art",
            new Dictionary<string, object?> { ["title"] = "T" }).Object);
        fixture.Nodes.Add(Doc(2, newsType, "news1",
            new Dictionary<string, object?> { ["author"] = "ann" }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection>
            {
                new() { Kind = "document", Alias = "article" },
                new() { Kind = "document", Alias = "news" },
            },
            columns: new List<AuditColumnDefinition> { Column("title"), Column("author") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        var results = await service.GetResultsAsync(
            UserA(), created.Value!.RunId, 0, 50, "kind", "asc", CancellationToken.None);

        Assert.Equal(2, results.Value!.TotalItems);
        var art = results.Value.Items.Single(i => i.OwnerDocumentName == "art");
        var news = results.Value.Items.Single(i => i.OwnerDocumentName == "news1");
        Assert.Equal("filled", art.Cells.Single(c => c.Alias == "title").Status);
        Assert.Equal("notApplicable", art.Cells.Single(c => c.Alias == "author").Status);
        Assert.Equal("notApplicable", news.Cells.Single(c => c.Alias == "title").Status);
        Assert.Equal("filled", news.Cells.Single(c => c.Alias == "author").Status);
    }

    [Fact]
    public async Task Filters_AreAnded_AcrossColumns()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: new[] { TextProp("title"), TextProp("author") });
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "both",
            new Dictionary<string, object?> { ["title"] = "abc", ["author"] = "ann" }).Object);
        fixture.Nodes.Add(Doc(2, type, "half",
            new Dictionary<string, object?> { ["title"] = "abc", ["author"] = "bob" }).Object);
        var service = fixture.Create();
        var definition = Definition(columns: new List<AuditColumnDefinition>
        {
            Column("title", filter: new AuditColumnFilter { Operator = "contains", Value = "ab" }),
            Column("author", filter: new AuditColumnFilter { Operator = "equals", Value = "ann" }),
        });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal(1, advance.Value!.TotalCount);
    }

    [Fact]
    public async Task EmptyValue_DistinctFromNotApplicable()
    {
        var fixture = new RunFixture();
        var title = TextProp("title");
        var author = TextProp("author");
        var type = DocType("article", 1, compositionProperties: title);
        var newsType = DocType("news", 2, compositionProperties: author);
        fixture.ContentTypes = new IContentType[] { type, newsType };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "" }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection>
            {
                new() { Kind = "document", Alias = "article" },
                new() { Kind = "document", Alias = "news" },
            },
            columns: new List<AuditColumnDefinition> { Column("title"), Column("author") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        var results = await service.GetResultsAsync(
            UserA(), created.Value!.RunId, 0, 50, null, null, CancellationToken.None);

        var row = results.Value!.Items.Single();
        Assert.Equal("empty", row.Cells.Single(c => c.Alias == "title").Status);
        Assert.Equal("notApplicable", row.Cells.Single(c => c.Alias == "author").Status);
    }

    [Fact]
    public async Task DeniedProperty_RestrictedCell_NotRead_NotMatched()
    {
        var fixture = new RunFixture();
        var title = TestBuilders.PropertyType("title", key: TitleKey).Object;
        var type = DocType("article", 1, compositionProperties: title);
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var node = Doc(1, type, "n", new Dictionary<string, object?> { ["title"] = "x" });
        fixture.Nodes.Add(node.Object);
        var service = fixture.Create();
        var user = User(UserAKey, deniedPropertyKey: TitleKey.ToString());
        var definition = Definition(columns: new List<AuditColumnDefinition>
        {
            Column("title"), Column("author"),
        });

        var create = await service.CreateAsync(user, Draft(definition), CancellationToken.None);
        // "author" undefined everywhere -> invalid; use only title, denied everywhere -> 403.
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden,
            create.Status);
        node.Verify(n => n.GetValue("title", It.IsAny<string?>(), It.IsAny<string?>(), false),
            Times.Never);
    }

    [Fact]
    public async Task OtherUsersRuns_Return404()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        var progress = await service.GetProgressAsync(UserB(), created.Value!.RunId, CancellationToken.None);
        var advance = await service.AdvanceAsync(UserB(), created.Value!.RunId, CancellationToken.None);
        var cancel = await service.CancelAsync(UserB(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.NotFound, progress.Status);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.NotFound, advance.Status);
        Assert.Equal(AuditExecutionService.RunResult<object>.StatusKind.NotFound, cancel.Status);
    }

    [Fact]
    public async Task SavedAudit_UsesCurrentViewerScope_UnsharedGives404()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var allowed = Doc(1, type, "visible",
            new Dictionary<string, object?> { ["title"] = "x" }).Object;
        var denied = Doc(2, type, "hidden",
            new Dictionary<string, object?> { ["title"] = "y" }).Object;
        fixture.Nodes.AddRange(new[] { allowed, denied });
        var record = new SavedQueryRecord
        {
            Key = Guid.NewGuid(),
            OwnerKey = UserAKey,
            Name = "shared",
            OwnerName = "A",
            IsShared = true,
            Version = 3,
            Definition = System.Text.Json.JsonSerializer.Serialize(
                Definition(), Camel),
        };
        fixture.VisibleRecords[record.Key] = record;
        // B cannot browse the "hidden" node.
        fixture.AuthorizationFilter = (user, node) =>
            !(user.Key == UserBKey && node.Name == "hidden");
        var service = fixture.Create();

        var created = await service.CreateAsync(
            UserB(), new AuditRunCreateRequest { AuditId = record.Key }, CancellationToken.None);
        var advance = await service.AdvanceAsync(UserB(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal(1, advance.Value!.TotalCount);
        Assert.Equal(1, advance.Value.ScannedCount);

        // Unsharing makes subsequent operations inaccessible.
        record.IsShared = false;
        var progress = await service.GetProgressAsync(UserB(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.NotFound,
            progress.Status);
    }

    [Fact]
    public async Task SavedAuditVersionChange_MarksRunStale()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var record = new SavedQueryRecord
        {
            Key = Guid.NewGuid(), OwnerKey = UserAKey, Name = "n", OwnerName = "A",
            IsShared = true, Version = 1,
            Definition = System.Text.Json.JsonSerializer.Serialize(
                Definition(), Camel),
        };
        fixture.VisibleRecords[record.Key] = record;
        var service = fixture.Create();

        var created = await service.CreateAsync(
            UserB(), new AuditRunCreateRequest { AuditId = record.Key }, CancellationToken.None);
        record.Version = 2;

        var progress = await service.GetProgressAsync(UserB(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Conflict,
            progress.Status);
    }

    [Fact]
    public async Task Culture_Unknown400_Denied403()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US"), TestBuilders.Language("da-DK") };
        var service = fixture.Create();

        var unknown = await service.CreateAsync(
            UserA(), Draft(Definition(culture: "fr-FR")), CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Invalid, unknown.Status);

        fixture.DeniedCultures.Add("da-DK");
        var denied = await service.CreateAsync(
            UserA(), Draft(Definition(culture: "da-DK")), CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden, denied.Status);
    }

    [Fact]
    public async Task TrashedNodes_AreSkippedWithoutAuthorization()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "trash",
            new Dictionary<string, object?> { ["title"] = "x" }, trashed: true).Object);
        fixture.Nodes.Add(Doc(2, type, "live",
            new Dictionary<string, object?> { ["title"] = "y" }).Object);
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal(1, advance.Value!.ScannedCount);
        Assert.Equal(1, advance.Value.TotalCount);
    }

    [Fact]
    public async Task DraftRun_NeverPersistsSummary()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        Assert.Empty(fixture.SummaryCalls);
    }

    [Fact]
    public async Task CompletedSavedRun_PersistsViewerScopedCount()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var record = new SavedQueryRecord
        {
            Key = Guid.NewGuid(), OwnerKey = UserAKey, Name = "n", OwnerName = "A",
            IsShared = true, Version = 4,
            Definition = System.Text.Json.JsonSerializer.Serialize(
                Definition(), Camel),
        };
        fixture.VisibleRecords[record.Key] = record;
        var service = fixture.Create();

        var created = await service.CreateAsync(
            UserB(), new AuditRunCreateRequest { AuditId = record.Key }, CancellationToken.None);
        await service.AdvanceAsync(UserB(), created.Value!.RunId, CancellationToken.None);

        var call = Assert.Single(fixture.SummaryCalls);
        Assert.Equal(UserBKey, call.User);
        Assert.Equal(record.Key, call.Audit);
        Assert.Equal(1, call.Count);
        Assert.Equal(4, call.Version);
    }

    // ---- results & sorting -------------------------------------------------------

    [Fact]
    public async Task Results_GlobalNumericSort_NA_LastBothDirections()
    {
        var fixture = new RunFixture();
        var rating = TextProp("rating");
        var other = TextProp("other");
        var article = DocType("article", 1, compositionProperties: rating);
        var news = DocType("news", 2, compositionProperties: other);
        fixture.ContentTypes = new IContentType[] { article, news };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, article, "ten",
            new Dictionary<string, object?> { ["rating"] = 10 }).Object);
        fixture.Nodes.Add(Doc(2, article, "two",
            new Dictionary<string, object?> { ["rating"] = 2 }).Object);
        fixture.Nodes.Add(Doc(3, news, "na",
            new Dictionary<string, object?> { ["other"] = "z" }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection>
            {
                new() { Kind = "document", Alias = "article" },
                new() { Kind = "document", Alias = "news" },
            },
            columns: new List<AuditColumnDefinition>
            {
                Column("rating", "number"), Column("other"),
            });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        var asc = await service.GetResultsAsync(UserA(), created.Value!.RunId, 0, 50,
            "property:rating", "asc", CancellationToken.None);
        Assert.Equal(new[] { "two", "ten", "na" },
            asc.Value!.Items.Select(i => i.OwnerDocumentName).ToArray());

        var desc = await service.GetResultsAsync(UserA(), created.Value!.RunId, 0, 50,
            "property:rating", "desc", CancellationToken.None);
        Assert.Equal(new[] { "ten", "two", "na" },
            desc.Value!.Items.Select(i => i.OwnerDocumentName).ToArray());
    }

    [Fact]
    public async Task Results_SortAcrossPages_TieBreakByRowKey()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        for (var i = 0; i < 5; i++)
        {
            fixture.Nodes.Add(Doc(i + 1, type, $"n{i}",
                new Dictionary<string, object?> { ["title"] = "same" }).Object);
        }
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        var page1 = await service.GetResultsAsync(UserA(), created.Value!.RunId, 0, 2,
            "property:title", "asc", CancellationToken.None);
        var page2 = await service.GetResultsAsync(UserA(), created.Value!.RunId, 2, 2,
            "property:title", "asc", CancellationToken.None);

        Assert.Equal(2, page1.Value!.Items.Count);
        Assert.Equal(5, page1.Value.TotalItems);
        Assert.Distinct(page1.Value.Items.Concat(page2.Value.Items).Select(i => i.RowKey));
    }

    [Fact]
    public async Task Results_InvalidPagingAndSort_400()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        var runId = created.Value!.RunId;

        Assert.Equal(AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Invalid,
            (await service.GetResultsAsync(UserA(), runId, -1, 50, null, null, CancellationToken.None)).Status);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Invalid,
            (await service.GetResultsAsync(UserA(), runId, 0, 0, null, null, CancellationToken.None)).Status);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Invalid,
            (await service.GetResultsAsync(UserA(), runId, 0, 201, null, null, CancellationToken.None)).Status);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Invalid,
            (await service.GetResultsAsync(UserA(), runId, 0, 50, "bogus", "asc", CancellationToken.None)).Status);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Invalid,
            (await service.GetResultsAsync(UserA(), runId, 0, 50, "kind", "sideways", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task RevokedPropertyRead_BeforeResults_403()
    {
        var fixture = new RunFixture();
        var title = TestBuilders.PropertyType("title", key: TitleKey).Object;
        var type = DocType("article", 1, compositionProperties: title);
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var service = fixture.Create();

        var user = User(UserAKey);
        var created = await service.CreateAsync(user, Draft(Definition()), CancellationToken.None);
        await service.AdvanceAsync(user, created.Value!.RunId, CancellationToken.None);

        // Simulate revocation: swap the user's group to deny the property key.
        var denied = User(UserAKey, deniedPropertyKey: TitleKey.ToString());
        var results = await service.GetResultsAsync(
            denied, created.Value!.RunId, 0, 50, null, null, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Forbidden,
            results.Status);
    }

    [Fact]
    public async Task RevokedBrowse_BeforeResults_FiltersRows()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var keep = Doc(1, type, "keep",
            new Dictionary<string, object?> { ["title"] = "x" }).Object;
        var drop = Doc(2, type, "drop",
            new Dictionary<string, object?> { ["title"] = "y" }).Object;
        fixture.Nodes.AddRange(new[] { keep, drop });
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        fixture.AuthorizationFilter = (_, node) => node.Name != "drop";
        var results = await service.GetResultsAsync(
            UserA(), created.Value!.RunId, 0, 50, null, null, CancellationToken.None);

        Assert.Equal(1, results.Value!.TotalItems);
        Assert.Equal("keep", results.Value.Items.Single().OwnerDocumentName);
        Assert.Equal(1, results.Value.Progress.TotalCount);
    }

    // ---- elements / blocks -----------------------------------------------------------

    [Fact]
    public async Task BlockList_ContentAndSettings_ProduceElementRows()
    {
        var fixture = new RunFixture();
        var settingsKey = Guid.NewGuid();
        var contentKey = Guid.NewGuid();
        var blocks = TextProp("blocks", BlocksKey, editorAlias: "Umbraco.BlockList");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var link = ElementType("link", 9, TextProp("title", ElTitleKey), TextProp("url"));
        fixture.ContentTypes = new IContentType[] { ownerType, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var json = $$"""
        {
          "layout": {"Umbraco.BlockList": [{"contentKey":"{{contentKey}}","settingsKey":"{{settingsKey}}"}]},
          "contentData": [{"key":"{{contentKey}}","contentTypeKey":"{{GuidForType("link")}}","values":[{"alias":"title","value":"home"}]}],
          "settingsData": [{"key":"{{settingsKey}}","contentTypeKey":"{{GuidForType("link")}}","values":[{"alias":"title","value":"s"}]}],
          "orphan": {"key":"{{Guid.NewGuid()}}","contentTypeKey":"{{GuidForType("link")}}"}
        }
        """;
        fixture.Nodes.Add(Doc(1, ownerType, "owner",
            new Dictionary<string, object?> { ["blocks"] = json }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection>
            {
                new() { Kind = "element", Alias = "link" },
            },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        var results = await service.GetResultsAsync(
            UserA(), created.Value!.RunId, 0, 50, "location", "asc", CancellationToken.None);

        // Content + settings instances; orphan contentData ignored.
        Assert.Equal(2, results.Value!.TotalItems);
        Assert.All(results.Value.Items, i => Assert.Equal("element", i.Kind));
        Assert.All(results.Value.Items, i => Assert.Equal("link", i.TypeAlias));
        Assert.Contains(results.Value.Items, i =>
            i.Cells.Single(c => c.Alias == "title").Value == "home");
    }

    [Fact]
    public async Task BlockGrid_Areas_ProduceRows()
    {
        var fixture = new RunFixture();
        var contentKey = Guid.NewGuid();
        var areaKey = Guid.NewGuid();
        var blocks = TextProp("grid", editorAlias: "Umbraco.BlockGrid");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var link = ElementType("link", 9, TextProp("title"));
        fixture.ContentTypes = new IContentType[] { ownerType, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var json = $$"""
        {
          "layout": {"Umbraco.BlockGrid": [
            {"contentKey":"{{contentKey}}"},
            {"contentKey":"{{Guid.NewGuid()}}","contentTypeKey":"x"}
          ],
          "areas": [{"key":"{{areaKey}}","items":[{"contentKey":"{{contentKey}}"}]}]},
          "contentData": [
            {"key":"{{contentKey}}","contentTypeKey":"{{GuidForType("link")}}","values":[{"alias":"title","value":"grid1"}]},
            {"key":"{{Guid.NewGuid()}}","contentTypeKey":"{{GuidForType("link")}}","values":[{"alias":"title","value":"unreferenced"}]}
          ]
        }
        """;
        fixture.Nodes.Add(Doc(1, ownerType, "owner",
            new Dictionary<string, object?> { ["grid"] = json }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "link" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        var results = await service.GetResultsAsync(
            UserA(), created.Value!.RunId, 0, 50, null, null, CancellationToken.None);

        // Only layout-referenced instances; unreferenced contentData ignored.
        Assert.Equal(1, results.Value!.TotalItems);
        Assert.Equal("grid1", results.Value.Items.Single().Cells.Single().Value);
    }

    [Fact]
    public async Task MalformedBlockJson_MarksPartial_Not500()
    {
        var fixture = new RunFixture();
        var blocks = TextProp("blocks", editorAlias: "Umbraco.BlockList");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var link = ElementType("link", 9, TextProp("title"));
        fixture.ContentTypes = new IContentType[] { ownerType, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, ownerType, "owner",
            new Dictionary<string, object?> { ["blocks"] = "{not json" }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "link" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal("partial", advance.Value!.Status);
        Assert.Null(advance.Value.TotalCount);
        Assert.NotEmpty(advance.Value.Warnings);
        Assert.Empty(fixture.SummaryCalls);
    }

    [Fact]
    public async Task HiddenBlockProperty_NeverParsed()
    {
        var fixture = new RunFixture();
        var blocks = TestBuilders.PropertyType("blocks", key: BlocksKey,
            editorAlias: "Umbraco.BlockList").Object;
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var link = ElementType("link", 9, TextProp("title"));
        fixture.ContentTypes = new IContentType[] { ownerType, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var node = Doc(1, ownerType, "owner",
            new Dictionary<string, object?> { ["blocks"] = "{}" });
        fixture.Nodes.Add(node.Object);
        var service = fixture.Create();
        var user = User(UserAKey, deniedPropertyKey: BlocksKey.ToString());
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "link" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(user, Draft(definition), CancellationToken.None);
        var advance = await service.AdvanceAsync(user, created.Value!.RunId, CancellationToken.None);

        Assert.Equal("completed", advance.Value!.Status);
        node.Verify(n => n.GetValue("blocks", It.IsAny<string?>(), It.IsAny<string?>(), false),
            Times.Never);
    }

    [Fact]
    public async Task SegmentVaryingProperty_Rejected()
    {
        var fixture = new RunFixture();
        var seg = TestBuilders.PropertyType("title",
            variations: ContentVariation.Segment).Object;
        var type = DocType("article", 1, compositionProperties: seg);
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Invalid,
            created.Status);
    }

    [Fact]
    public async Task UnknownElementType_AddsPartialWarning()
    {
        var fixture = new RunFixture();
        var blocks = TextProp("blocks", editorAlias: "Umbraco.BlockList");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var link = ElementType("link", 9, TextProp("title"));
        fixture.ContentTypes = new IContentType[] { ownerType, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var json = $$"""
        {"layout":{"Umbraco.BlockList":[{"contentKey":"{{Guid.NewGuid()}}"}]},
         "contentData":[{"key":"{{Guid.NewGuid()}}","contentTypeKey":"{{Guid.NewGuid()}}","values":[]}]}
        """;
        // Layout references a content key not present in contentData.
        var contentKey = Guid.NewGuid();
        json = $$"""
        {"layout":{"Umbraco.BlockList":[{"contentKey":"{{contentKey}}"}]},
         "contentData":[{"key":"{{contentKey}}","contentTypeKey":"{{Guid.NewGuid()}}","values":[{"alias":"title","value":"x"}]}]}
        """;
        fixture.Nodes.Add(Doc(1, ownerType, "owner",
            new Dictionary<string, object?> { ["blocks"] = json }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "link" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal("partial", advance.Value!.Status);
        Assert.NotEmpty(advance.Value.Warnings);
    }

    [Fact]
    public async Task ElementSelection_ScansAllDocumentOwners()
    {
        var fixture = new RunFixture();
        var contentKey = Guid.NewGuid();
        var blocks = TextProp("blocks", editorAlias: "Umbraco.BlockList");
        var withBlocks = DocType("article", 1, compositionProperties: blocks);
        var plain = DocType("news", 2, compositionProperties: TextProp("title"));
        var link = ElementType("link", 9, TextProp("title"));
        fixture.ContentTypes = new IContentType[] { withBlocks, plain, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var json = $$"""
        {"layout":{"Umbraco.BlockList":[{"contentKey":"{{contentKey}}"}]},
         "contentData":[{"key":"{{contentKey}}","contentTypeKey":"{{GuidForType("link")}}","values":[{"alias":"title","value":"el"}]}]}
        """;
        fixture.Nodes.Add(Doc(1, withBlocks, "w",
            new Dictionary<string, object?> { ["blocks"] = json }).Object);
        fixture.Nodes.Add(Doc(2, plain, "p",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "link" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        // Both document types scanned as owners even though only news defines title.
        Assert.Equal(2, advance.Value!.ScannedCount);
        Assert.Equal(1, advance.Value.TotalCount);
    }

    [Fact]
    public async Task LimitedRun_NullTotal_NoSummary()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        var runId = created.Value!.RunId;
        // Pre-fill retained rows to the cap; a further matching row cannot be
        // retained so the run must report limited, never an exact count.
        var state = fixture.Store.GetForOwner(runId, UserAKey, DateTime.UtcNow)!;
        for (var i = 0; i < AuditExecutionService.MaxRetainedRows; i++)
        {
            state.Rows.Add(new AuditRunStore.RetainedRow
            {
                RowKey = $"r{i}", Kind = "document", TypeAlias = "article",
                TypeName = "article", TypeKey = type.Key,
                OwnerDocumentKey = Guid.NewGuid(), OwnerDocumentName = "n",
                Location = "Content", EditUrl = "/u", Cells = new(),
            });
        }

        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var advance = await service.AdvanceAsync(UserA(), runId, CancellationToken.None);
        Assert.Equal("limited", advance.Value!.Status);
        Assert.Null(advance.Value.TotalCount);
        Assert.NotEmpty(advance.Value.Warnings);
        Assert.Empty(fixture.SummaryCalls);
    }

    [Fact]
    public async Task ExactBoundary_AllCandidatesExamined_CompletesNotLimited()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal("completed", advance.Value!.Status);
        Assert.Equal(1, advance.Value.TotalCount);
    }

    [Fact]
    public async Task RetainedCharacterCap_LimitsRun()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "ab" }).Object);
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        var state = fixture.Store.GetForOwner(
            created.Value!.RunId, UserAKey, DateTime.UtcNow)!;
        state.RetainedCharacters = AuditExecutionService.MaxRetainedCharacters - 1;

        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal("limited", advance.Value!.Status);
        Assert.Null(advance.Value.TotalCount);
    }

    [Fact]
    public async Task RevokedPropertyRead_AfterCompletion_Progress403_NoSummary()
    {
        var fixture = new RunFixture();
        var title = TestBuilders.PropertyType("title", key: TitleKey).Object;
        var type = DocType("article", 1, compositionProperties: title);
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var record = new SavedQueryRecord
        {
            Key = Guid.NewGuid(), OwnerKey = UserAKey, Name = "n", OwnerName = "A",
            IsShared = true, Version = 1,
            Definition = System.Text.Json.JsonSerializer.Serialize(Definition(), Camel),
        };
        fixture.VisibleRecords[record.Key] = record;
        var service = fixture.Create();

        var created = await service.CreateAsync(
            UserA(), new AuditRunCreateRequest { AuditId = record.Key }, CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal("completed", advance.Value!.Status);
        Assert.Single(fixture.SummaryCalls);

        // Revoking property read invalidates progress too, not only results.
        var denied = User(UserAKey, deniedPropertyKey: TitleKey.ToString());
        var progress = await service.GetProgressAsync(denied, created.Value!.RunId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden,
            progress.Status);
    }

    [Fact]
    public async Task RemovedProperty_AfterCompletion_Progress409()
    {
        var fixture = new RunFixture();
        var title = TextProp("title");
        var type = DocType("article", 1, compositionProperties: title);
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(Doc(1, type, "n",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var service = fixture.Create();

        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        // Property removed from the schema after the run.
        fixture.ContentTypes = new[] { DocType("article", 1) };
        var progress = await service.GetProgressAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Conflict,
            progress.Status);
    }

    [Fact]
    public async Task NestedSelectedChild_InsideUnselectedWrapper_StillFound()
    {
        var fixture = new RunFixture();
        var wrapperKey = Guid.NewGuid();
        var childKey = Guid.NewGuid();
        var blocks = TextProp("blocks", editorAlias: "Umbraco.BlockList");
        var nested = TextProp("nested", editorAlias: "Umbraco.BlockList");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var wrapper = ElementType("wrapper", 8, nested);
        var child = ElementType("child", 9, TextProp("title"));
        fixture.ContentTypes = new IContentType[] { ownerType, wrapper, child };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var nestedJson = $$"""
        {"layout":{"Umbraco.BlockList":[{"contentKey":"{{childKey}}"}]},
         "contentData":[{"key":"{{childKey}}","contentTypeKey":"{{GuidForType("child")}}","values":[{"alias":"title","value":"deep"}]}]}
        """;
        var json = $$"""
        {"layout":{"Umbraco.BlockList":[{"contentKey":"{{wrapperKey}}"}]},
         "contentData":[{"key":"{{wrapperKey}}","contentTypeKey":"{{GuidForType("wrapper")}}","values":[{"alias":"nested","value":{{System.Text.Json.JsonSerializer.Serialize(nestedJson, Camel)}}}]}]}
        """;
        fixture.Nodes.Add(Doc(1, ownerType, "owner",
            new Dictionary<string, object?> { ["blocks"] = json }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "child" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var advance = await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);

        Assert.Equal("completed", advance.Value!.Status);
        Assert.Equal(1, advance.Value.TotalCount);
        var results = await service.GetResultsAsync(
            UserA(), created.Value!.RunId, 0, 50, null, null, CancellationToken.None);
        Assert.Equal("child", results.Value!.Items.Single().TypeAlias);
        Assert.Contains("nested", results.Value.Items.Single().Location);
    }

    [Fact]
    public async Task VariantName_MissingSelectedCulture_UsesUnnamedNotInvariant()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1,
            variations: ContentVariation.Culture,
            compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US"), TestBuilders.Language("da-DK") };
        // Node's invariant-ish name must not leak when the da-DK name is absent.
        fixture.Nodes.Add(Doc(1, type, "English name",
            new Dictionary<string, object?> { ["title"] = "x" }).Object);
        var service = fixture.Create();

        var created = await service.CreateAsync(
            UserA(), Draft(Definition(culture: "da-DK")), CancellationToken.None);
        await service.AdvanceAsync(UserA(), created.Value!.RunId, CancellationToken.None);
        var results = await service.GetResultsAsync(
            UserA(), created.Value!.RunId, 0, 50, null, null, CancellationToken.None);

        Assert.Equal("(unnamed)", results.Value!.Items.Single().OwnerDocumentName);
    }

    [Fact]
    public async Task ConcurrentCreates_RespectPerUserCap()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var service = fixture.Create();

        var tasks = Enumerable.Range(0, AuditRunStore.MaxRunsPerUser + 3)
            .Select(_ => service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(AuditRunStore.MaxRunsPerUser,
            results.Count(r => r.Status == AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Ok));
        Assert.Equal(3,
            results.Count(r => r.Status == AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.TooMany));
    }

    [Fact]
    public async Task AdvanceAndProgress_Concurrent_NoTornCount()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        for (var i = 0; i < 30; i++)
        {
            fixture.Nodes.Add(Doc(i + 1, type, $"n{i}",
                new Dictionary<string, object?> { ["title"] = $"t{i}" }).Object);
        }
        var service = fixture.Create();
        var created = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        var runId = created.Value!.RunId;

        var advance = service.AdvanceAsync(UserA(), runId, CancellationToken.None);
        var progress = await service.GetProgressAsync(UserA(), runId, CancellationToken.None);
        var advanced = await advance;

        // Progress was taken either before or after the batch, never torn.
        Assert.True(progress.Value!.ScannedCount is 0 or 30);
        Assert.Equal("completed", advanced.Value!.Status);
        Assert.Equal(30, advanced.Value.TotalCount);
    }

    [Fact]
    public async Task PerUserRunCap_Returns429()
    {
        var fixture = new RunFixture();
        var type = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new[] { type };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var service = fixture.Create();

        for (var i = 0; i < AuditRunStore.MaxRunsPerUser; i++)
        {
            var ok = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
            Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Ok, ok.Status);
        }

        var overflow = await service.CreateAsync(UserA(), Draft(Definition()), CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.TooMany,
            overflow.Status);
    }

    // ---- traversal-access revalidation -----------------------------------------

    private static Mock<IContent> DocWithBlocks(
        IContentType ownerType, Guid contentKey, string elementAlias)
    {
        var json = $$"""
        {"layout":{"Umbraco.BlockList":[{"contentKey":"{{contentKey}}"}]},
         "contentData":[{"key":"{{contentKey}}","contentTypeKey":"{{GuidForType(elementAlias)}}","values":[{"alias":"title","value":"el"}]}]}
        """;
        return Doc(1, ownerType, "owner",
            new Dictionary<string, object?> { ["blocks"] = json });
    }

    [Fact]
    public async Task RootBlockAccessRevoked_ProgressAndResults403()
    {
        var fixture = new RunFixture();
        var contentKey = Guid.NewGuid();
        var blocks = TextProp("blocks", key: BlocksKey, editorAlias: "Umbraco.BlockList");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var link = ElementType("link", 9, TextProp("title", key: ElTitleKey));
        fixture.ContentTypes = new IContentType[] { ownerType, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(DocWithBlocks(ownerType, contentKey, "link").Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "link" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var runId = created.Value!.RunId;
        var advance = await service.AdvanceAsync(UserA(), runId, CancellationToken.None);
        Assert.Equal(1, advance.Value!.TotalCount);

        // Revoke the root block property only; the element title stays readable.
        var revoked = User(UserAKey, deniedPropertyKey: BlocksKey.ToString());
        var progress = await service.GetProgressAsync(revoked, runId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden,
            progress.Status);
        var results = await service.GetResultsAsync(
            revoked, runId, 0, 50, null, null, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Forbidden,
            results.Status);
    }

    [Fact]
    public async Task NestedWrapperAccessRevoked_Progress403()
    {
        var fixture = new RunFixture();
        var wrapperKey = Guid.NewGuid();
        var childKey = Guid.NewGuid();
        var blocks = TextProp("blocks", key: BlocksKey, editorAlias: "Umbraco.BlockList");
        var nested = TextProp("nested", key: HiddenKey, editorAlias: "Umbraco.BlockList");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var wrapper = ElementType("wrapper", 8, nested);
        var child = ElementType("child", 9, TextProp("title", key: ElTitleKey));
        fixture.ContentTypes = new IContentType[] { ownerType, wrapper, child };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        var nestedJson = $$"""
        {"layout":{"Umbraco.BlockList":[{"contentKey":"{{childKey}}"}]},
         "contentData":[{"key":"{{childKey}}","contentTypeKey":"{{GuidForType("child")}}","values":[{"alias":"title","value":"deep"}]}]}
        """;
        var json = $$"""
        {"layout":{"Umbraco.BlockList":[{"contentKey":"{{wrapperKey}}"}]},
         "contentData":[{"key":"{{wrapperKey}}","contentTypeKey":"{{GuidForType("wrapper")}}","values":[{"alias":"nested","value":{{System.Text.Json.JsonSerializer.Serialize(nestedJson, Camel)}}}]}]}
        """;
        fixture.Nodes.Add(Doc(1, ownerType, "owner",
            new Dictionary<string, object?> { ["blocks"] = json }).Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "child" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var runId = created.Value!.RunId;
        var advance = await service.AdvanceAsync(UserA(), runId, CancellationToken.None);
        Assert.Equal(1, advance.Value!.TotalCount);

        // The wrapper's nested block property is denied while the child title
        // remains readable.
        var revoked = User(UserAKey, deniedPropertyKey: HiddenKey.ToString());
        var progress = await service.GetProgressAsync(revoked, runId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden,
            progress.Status);
    }

    [Fact]
    public async Task AncestorPropertyRemoved_Progress409()
    {
        var fixture = new RunFixture();
        var contentKey = Guid.NewGuid();
        var blocks = TextProp("blocks", key: BlocksKey, editorAlias: "Umbraco.BlockList");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var link = ElementType("link", 9, TextProp("title", key: ElTitleKey));
        fixture.ContentTypes = new IContentType[] { ownerType, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(DocWithBlocks(ownerType, contentKey, "link").Object);
        var service = fixture.Create();
        var definition = Definition(
            types: new List<AuditTypeSelection> { new() { Kind = "element", Alias = "link" } },
            columns: new List<AuditColumnDefinition> { Column("title") });

        var created = await service.CreateAsync(UserA(), Draft(definition), CancellationToken.None);
        var runId = created.Value!.RunId;
        await service.AdvanceAsync(UserA(), runId, CancellationToken.None);

        // The owner type survives but loses the traversal property.
        var trimmed = DocType("article", 1, compositionProperties: TextProp("title"));
        fixture.ContentTypes = new IContentType[] { trimmed, link };

        var progress = await service.GetProgressAsync(UserA(), runId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Conflict,
            progress.Status);
    }

    [Fact]
    public async Task SavedRun_AncestorRevokedBetweenBatches_Advance403_NoSummary()
    {
        var fixture = new RunFixture();
        var contentKey = Guid.NewGuid();
        var blocks = TextProp("blocks", key: BlocksKey, editorAlias: "Umbraco.BlockList");
        var ownerType = DocType("article", 1, compositionProperties: blocks);
        var link = ElementType("link", 9, TextProp("title", key: ElTitleKey));
        fixture.ContentTypes = new IContentType[] { ownerType, link };
        fixture.Languages = new[] { TestBuilders.Language("en-US") };
        fixture.Nodes.Add(DocWithBlocks(ownerType, contentKey, "link").Object);
        var record = new SavedQueryRecord
        {
            Key = Guid.NewGuid(), OwnerKey = UserAKey, Name = "n", OwnerName = "A",
            IsShared = true, Version = 1,
            Definition = System.Text.Json.JsonSerializer.Serialize(
                Definition(
                    types: new List<AuditTypeSelection>
                        { new() { Kind = "element", Alias = "link" } },
                    columns: new List<AuditColumnDefinition> { Column("title") }),
                Camel),
        };
        fixture.VisibleRecords[record.Key] = record;
        var service = fixture.Create();

        var created = await service.CreateAsync(
            UserA(), new AuditRunCreateRequest { AuditId = record.Key }, CancellationToken.None);
        var runId = created.Value!.RunId;
        var first = await service.AdvanceAsync(UserA(), runId, CancellationToken.None);
        Assert.Equal("completed", first.Value!.Status);
        Assert.Single(fixture.SummaryCalls);

        // Revoke the root block property, then progress/advance again.
        var revoked = User(UserAKey, deniedPropertyKey: BlocksKey.ToString());
        var progress = await service.GetProgressAsync(revoked, runId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden,
            progress.Status);
        var advance = await service.AdvanceAsync(revoked, runId, CancellationToken.None);
        Assert.Equal(AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden,
            advance.Status);
        // No additional summary may be persisted after revocation.
        Assert.Single(fixture.SummaryCalls);
    }
}

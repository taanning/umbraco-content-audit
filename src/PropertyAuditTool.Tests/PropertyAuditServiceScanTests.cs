using Moq;
using PropertyAuditTool.Models;
using PropertyAuditTool.Services;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Xunit;

namespace PropertyAuditTool.Tests;

public class PropertyAuditServiceScanTests
{
    private const string Alias = "description";

    private static (ServiceFixture fixture, IPropertyType propertyType) FixtureWithType(
        ContentVariation typeVariations = ContentVariation.Nothing,
        ContentVariation propertyVariations = ContentVariation.Nothing,
        IReadOnlyList<ILanguage>? languages = null)
    {
        var propertyType = TestBuilders.PropertyType(Alias, variations: propertyVariations);
        var fixture = new ServiceFixture
        {
            ContentTypes = [TestBuilders.ContentType(
                "article", 1, variations: typeVariations, compositionProperties: propertyType.Object).Object],
            Languages = languages ?? [TestBuilders.Language("en-US", isDefault: true)],
        };
        return (fixture, propertyType.Object);
    }

    private Mock<IContent> AddNode(
        ServiceFixture fixture,
        IPropertyType propertyType,
        int id,
        object? value,
        bool trashed = false,
        bool hasProperty = true,
        string name = "node",
        string? cultureName = null,
        ISimpleContentType? contentType = null)
    {
        var node = TestBuilders.Content(
            id,
            Alias,
            contentType ?? TestBuilders.SimpleContentType("article", id: 1).Object,
            hasProperty: hasProperty,
            property: hasProperty ? TestBuilders.Property(propertyType).Object : null,
            propertyValue: value,
            trashed: trashed,
            name: name,
            cultureName: cultureName);
        fixture.Nodes.Add(node.Object);
        return node;
    }

    private static AuditQueryRequest Request(
        string op = "isEmpty",
        string? search = null,
        string? culture = null,
        long skip = 0,
        int take = 50,
        string? docTypeAlias = null) =>
        new()
        {
            DocumentTypeAlias = docTypeAlias,
            PropertyAlias = Alias,
            Operator = op,
            SearchValue = search,
            Culture = culture,
            Skip = skip,
            Take = take,
        };

    [Fact]
    public async Task Match_ReturnsItemFields_NoValues()
    {
        var (fixture, propertyType) = FixtureWithType();
        var node = AddNode(fixture, propertyType, 10, "   ", name: "My Page");

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(10, item.NodeId);
        Assert.Equal(node.Object.Key, item.Unique);
        Assert.Equal("My Page", item.Name);
        Assert.Equal("article", item.DocumentTypeAlias);
        Assert.Equal("Empty", item.PropertyStatus);
        Assert.Contains(node.Object.Key.ToString("D"), item.EditUrl);
        Assert.Equal(1, result.Value.ScannedCount);
    }

    [Fact]
    public async Task TrashedAndDeniedNodes_AreSkipped_ButPreserveContinuation()
    {
        var (fixture, propertyType) = FixtureWithType();
        AddNode(fixture, propertyType, 1, "   ", trashed: true);
        var denied = AddNode(fixture, propertyType, 2, "   ");
        AddNode(fixture, propertyType, 3, "   ");
        fixture.AuthorizationFilter = node => node.Key != denied.Object.Key;

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(3, item.NodeId);
        Assert.Equal(1, result.Value.ScannedCount);
        Assert.Null(result.Value.NextSkip); // exhausted: all 3 raw candidates visited
    }

    [Fact]
    public async Task NodeLackingProperty_IsExcluded_NotScanned()
    {
        var (fixture, propertyType) = FixtureWithType();
        AddNode(fixture, propertyType, 1, "   ", hasProperty: false);
        AddNode(fixture, propertyType, 2, "   ");

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Value!.Items);
        Assert.Equal(1, result.Value.ScannedCount); // only the node defining the alias
    }

    [Fact]
    public async Task InvariantProperty_ReadsInvariantValue_EvenWhenCultureSupplied()
    {
        var (fixture, propertyType) = FixtureWithType();
        var node = AddNode(fixture, propertyType, 1, "   ");

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(culture: "en-US"), CancellationToken.None);

        Assert.True(result.Succeeded);
        node.Verify(c => c.GetValue(Alias, null, null, false), Times.Once);
    }

    [Fact]
    public async Task VariantProperty_ReadsSelectedCulture_Unpublished()
    {
        var (fixture, propertyType) = FixtureWithType(
            propertyVariations: ContentVariation.Culture,
            languages: [
                TestBuilders.Language("en-US", isDefault: true),
                TestBuilders.Language("da-DK"),
            ]);
        var node = AddNode(fixture, propertyType, 1, "   ");

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(culture: "da-dk"), CancellationToken.None);

        Assert.True(result.Succeeded);
        // Supplied ISO is normalized to the configured ISO code.
        node.Verify(c => c.GetValue(Alias, "da-DK", null, false), Times.Once);
    }

    [Fact]
    public async Task SavedDraft_IsIncluded()
    {
        var (fixture, propertyType) = FixtureWithType();
        var node = AddNode(fixture, propertyType, 1, "   ");
        node.SetupGet(c => c.Published).Returns(false);

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Value!.Items);
    }

    [Fact]
    public async Task TakeStopsInsidePage_NextSkipContinuesWithoutDuplicates()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 5; i++)
        {
            AddNode(fixture, propertyType, i, "   ");
        }

        var service = fixture.Create();
        var user = TestBuilders.ReadableUser();

        var first = await service.QueryAsync(user, Request(take: 2), CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(new[] { 1, 2 }, first.Value!.Items.Select(i => i.NodeId));
        Assert.Equal(2, first.Value.NextSkip); // raw cursor immediately after second match

        var second = await service.QueryAsync(
            user, Request(take: 2, skip: first.Value.NextSkip!.Value), CancellationToken.None);
        Assert.Equal(new[] { 3, 4 }, second.Value!.Items.Select(i => i.NodeId));
        Assert.Equal(4, second.Value.NextSkip);
    }

    [Fact]
    public async Task CursorCrossesDatabasePageBoundary()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 260; i++)
        {
            AddNode(fixture, propertyType, i, "   ");
        }

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(skip: 249, take: 3), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { 250, 251, 252 }, result.Value!.Items.Select(i => i.NodeId));
        Assert.Equal(252, result.Value.NextSkip);
    }

    [Fact]
    public async Task ScanWindowCap_LargeCandidateSet_ContinuesToTail()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 2001; i++)
        {
            AddNode(fixture, propertyType, i, "filled"); // non-matching for isEmpty
        }
        var service = fixture.Create();
        var user = TestBuilders.ReadableUser();

        var first = await service.QueryAsync(user, Request(take: 50), CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Empty(first.Value!.Items);
        Assert.Equal(2000, first.Value.NextSkip); // raw cursor at the scan-window boundary
        Assert.Equal(2000, first.Value.ScannedCount);

        var second = await service.QueryAsync(
            user, Request(take: 50, skip: 2000), CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Empty(second.Value!.Items);
        Assert.Null(second.Value.NextSkip); // tail reached
        Assert.Equal(1, second.Value.ScannedCount);
    }

    [Fact]
    public async Task AllDenied_YieldsEmptyPage_ExhaustedWhenAllVisited()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 5; i++)
        {
            AddNode(fixture, propertyType, i, "   ");
        }
        fixture.AuthorizationFilter = _ => false;

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(take: 1), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        Assert.Equal(0, result.Value.ScannedCount);
        // Candidates were visited but none exhausted the take limit; cursor advanced to end.
        Assert.Null(result.Value.NextSkip);
    }

    [Fact]
    public async Task PartiallyAuthorized_EmptyPage_CanStillContinue()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 5; i++)
        {
            AddNode(fixture, propertyType, i, "filled");
        }
        fixture.AuthorizationFilter = _ => false;

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(take: 50), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        Assert.Null(result.Value.NextSkip); // all 5 candidates visited → exhausted
    }

    [Fact]
    public async Task Cancellation_StopsScan()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 10; i++)
        {
            AddNode(fixture, propertyType, i, "   ");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Create().QueryAsync(TestBuilders.ReadableUser(), Request(), cts.Token));
    }

    [Fact]
    public async Task FullyAuthorizedBatch_CallsAuthorizerOnce_FastPath()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 4; i++)
        {
            AddNode(fixture, propertyType, i, "   ");
        }

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(take: 1), CancellationToken.None);

        Assert.True(result.Succeeded);
        // take=1 stops inside the single page → exactly one batch authorization.
        fixture.Authorizer.Verify(
            s => s.IsDeniedAsync(
                It.IsAny<IUser>(),
                It.Is<IEnumerable<Guid>>(keys => keys.Count() == 4),
                It.IsAny<ISet<string>>()),
            Times.Once);
    }

    [Fact]
    public async Task MixedAuthorizedBatch_SplitsAndExcludesDenied()
    {
        var (fixture, propertyType) = FixtureWithType();
        var denied1 = AddNode(fixture, propertyType, 1, "   ");
        var denied2 = AddNode(fixture, propertyType, 3, "   ");
        AddNode(fixture, propertyType, 2, "   ");
        AddNode(fixture, propertyType, 4, "   ");
        fixture.AuthorizationFilter = node =>
            node.Key != denied1.Object.Key && node.Key != denied2.Object.Key;

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { 2, 4 }, result.Value!.Items.Select(i => i.NodeId));
        Assert.Equal(2, result.Value.ScannedCount);
        // Denied singleton batches resolve to excluded keys without blanket approval.
        fixture.Authorizer.Verify(
            s => s.IsDeniedAsync(
                It.IsAny<IUser>(),
                It.Is<IEnumerable<Guid>>(keys => keys.Count() == 1),
                It.IsAny<ISet<string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task AllTrashed_NeverCallsAuthorizer()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 3; i++)
        {
            AddNode(fixture, propertyType, i, "   ", trashed: true);
        }

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        fixture.Authorizer.Verify(
            s => s.IsDeniedAsync(
                It.IsAny<IUser>(),
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<ISet<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task CancelledAuthorization_Propagates()
    {
        var (fixture, propertyType) = FixtureWithType();
        for (var i = 1; i <= 10; i++)
        {
            AddNode(fixture, propertyType, i, "   ");
        }

        var service = fixture.Create();
        var callCount = 0;
        using var cts = new CancellationTokenSource();
        fixture.Authorizer
            .Setup(s => s.IsDeniedAsync(
                It.IsAny<IUser>(),
                It.IsAny<IEnumerable<Guid>>(),
                It.IsAny<ISet<string>>()))
            .ReturnsAsync((IUser _, IEnumerable<Guid> keys, ISet<string> _) =>
            {
                if (++callCount > 1)
                {
                    cts.Cancel();
                }
                return keys.Count() > 1;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.QueryAsync(TestBuilders.ReadableUser(), Request(), cts.Token));
    }

    [Fact]
    public async Task VariantType_NameUsesCultureName()
    {
        var (fixture, propertyType) = FixtureWithType(typeVariations: ContentVariation.Culture);
        AddNode(
            fixture,
            propertyType,
            1,
            "   ",
            cultureName: "Dansk side",
            contentType: TestBuilders.SimpleContentType("article", id: 1, variations: ContentVariation.Culture).Object);

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(culture: "en-US"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Dansk side", result.Value!.Items[0].Name);
    }
}

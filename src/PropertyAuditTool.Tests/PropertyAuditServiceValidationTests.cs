using PropertyAuditTool.Models;
using PropertyAuditTool.Services;
using Umbraco.Cms.Core.Models;
using Xunit;

namespace PropertyAuditTool.Tests;

public class PropertyAuditServiceValidationTests
{
    private static ServiceFixture Fixture(
        IReadOnlyList<IContentType>? types = null,
        IReadOnlyList<ILanguage>? languages = null)
    {
        var fixture = new ServiceFixture();
        fixture.ContentTypes = types ?? [];
        fixture.Languages = languages ?? [TestBuilders.Language("en-US", isDefault: true)];
        return fixture;
    }

    private static AuditQueryRequest Request(
        string? propertyAlias = "description",
        string? op = "isEmpty",
        string? docTypeAlias = null,
        string? searchValue = null,
        string? culture = null,
        long skip = 0,
        int take = 50) =>
        new()
        {
            DocumentTypeAlias = docTypeAlias,
            PropertyAlias = propertyAlias,
            Operator = op,
            SearchValue = searchValue,
            Culture = culture,
            Skip = skip,
            Take = take,
        };

    [Fact]
    public async Task MissingPropertyAlias_Returns400()
    {
        var result = await Fixture().Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(propertyAlias: "  "), CancellationToken.None);
        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("propertyAlias"));
    }

    [Theory]
    [InlineData("IsEmpty")]   // case-sensitive: only exact operators allowed
    [InlineData("startswith")]
    [InlineData("")]
    public async Task UnsupportedOperator_Returns400(string op)
    {
        var result = await Fixture().Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(op: op), CancellationToken.None);
        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("operator"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Contains_RequiresNonBlankSearchValue(string? search)
    {
        var result = await Fixture().Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(op: "contains", searchValue: search), CancellationToken.None);
        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("searchValue"));
    }

    [Fact]
    public async Task Equals_RequiresNonBlankSearchValue()
    {
        var result = await Fixture().Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(op: "equals", searchValue: " "), CancellationToken.None);
        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("searchValue"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-500)]
    public async Task NegativeSkip_Returns400(long skip)
    {
        var result = await Fixture().Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(skip: skip), CancellationToken.None);
        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("skip"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(201)]
    public async Task InvalidTake_Returns400_NoClamping(int take)
    {
        var result = await Fixture().Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(take: take), CancellationToken.None);
        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("take"));
    }

    [Fact]
    public async Task UnknownDocumentTypeAlias_Returns400()
    {
        var prop = TestBuilders.PropertyType("description");
        var fixture = Fixture(types: [TestBuilders.ContentType("article", 1, compositionProperties: prop.Object).Object]);

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(docTypeAlias: "missing"), CancellationToken.None);

        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("documentTypeAlias"));
    }

    [Fact]
    public async Task UnknownPropertyAlias_Returns400()
    {
        var prop = TestBuilders.PropertyType("title");
        var fixture = Fixture(types: [TestBuilders.ContentType("article", 1, compositionProperties: prop.Object).Object]);

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(propertyAlias: "description"), CancellationToken.None);

        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("propertyAlias"));
    }

    [Fact]
    public async Task ExistingAliasWithoutReadablePermission_Returns403()
    {
        var prop = TestBuilders.PropertyType("description");
        var fixture = Fixture(types: [TestBuilders.ContentType("article", 1, compositionProperties: prop.Object).Object]);
        var userWithoutRead = TestBuilders.User(TestBuilders.UserGroup());

        var result = await fixture.Create().QueryAsync(
            userWithoutRead, Request(), CancellationToken.None);

        Assert.Equal(AuditErrorType.Forbidden, result.ErrorType);
    }

    [Fact]
    public async Task SegmentVaryingProperty_Returns400()
    {
        var prop = TestBuilders.PropertyType("description", variations: ContentVariation.Segment);
        var fixture = Fixture(types: [TestBuilders.ContentType("article", 1, compositionProperties: prop.Object).Object]);

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("propertyAlias"));
    }

    [Fact]
    public async Task VariantType_RequiresCulture()
    {
        var prop = TestBuilders.PropertyType("description");
        var fixture = Fixture(types: [
            TestBuilders.ContentType("article", 1, variations: ContentVariation.Culture, compositionProperties: prop.Object).Object,
        ]);

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("culture"));
    }

    [Fact]
    public async Task VariantProperty_RequiresCulture_EvenOnInvariantType()
    {
        var prop = TestBuilders.PropertyType("description", variations: ContentVariation.Culture);
        var fixture = Fixture(types: [TestBuilders.ContentType("article", 1, compositionProperties: prop.Object).Object]);

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(), CancellationToken.None);

        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("culture"));
    }

    [Fact]
    public async Task UnknownCulture_Returns400()
    {
        var prop = TestBuilders.PropertyType("description");
        var fixture = Fixture(types: [TestBuilders.ContentType("article", 1, compositionProperties: prop.Object).Object]);

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(culture: "xx-NOPE"), CancellationToken.None);

        Assert.Equal(AuditErrorType.Validation, result.ErrorType);
        Assert.True(result.Errors!.ContainsKey("culture"));
    }

    [Fact]
    public async Task DeniedCulture_Returns403_EvenForInvariantProperty()
    {
        var prop = TestBuilders.PropertyType("description");
        var fixture = Fixture(types: [TestBuilders.ContentType("article", 1, compositionProperties: prop.Object).Object]);
        fixture.DeniedCultures.Add("da-DK");
        fixture.Languages = [
            TestBuilders.Language("en-US", isDefault: true),
            TestBuilders.Language("da-DK"),
        ];

        var result = await fixture.Create().QueryAsync(
            TestBuilders.ReadableUser(), Request(culture: "da-dk"), CancellationToken.None);

        Assert.Equal(AuditErrorType.Forbidden, result.ErrorType);
    }
}

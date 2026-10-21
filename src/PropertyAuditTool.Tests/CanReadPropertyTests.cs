using PropertyAuditTool.Services;
using Xunit;

namespace PropertyAuditTool.Tests;

public class CanReadPropertyTests
{
    private const string Read = "Umb.Document.PropertyValue.Read";
    private const string Write = "Umb.Document.PropertyValue.Write";

    [Fact]
    public void FallbackGroupPermission_AllowsRead()
    {
        var prop = TestBuilders.PropertyType("description").Object;
        var user = TestBuilders.User(TestBuilders.UserGroup(permissions: [Read]));
        Assert.True(PropertyAuditService.CanReadProperty(user, prop));
    }

    [Fact]
    public void NoPermissions_DeniesRead()
    {
        var prop = TestBuilders.PropertyType("description").Object;
        var user = TestBuilders.User(TestBuilders.UserGroup());
        Assert.False(PropertyAuditService.CanReadProperty(user, prop));
    }

    [Fact]
    public void GranularWriteOnly_OverridesFallbackRead()
    {
        var prop = TestBuilders.PropertyType("description").Object;
        var user = TestBuilders.User(TestBuilders.UserGroup(
            permissions: [Read],
            granularPermissions: [
                TestBuilders.PropertyValueGranular(prop.Key, $"{prop.Key:D}|{Write}"),
            ]));
        Assert.False(PropertyAuditService.CanReadProperty(user, prop));
    }

    [Fact]
    public void GranularEmptyVerb_IsExplicitDenial()
    {
        var prop = TestBuilders.PropertyType("description").Object;
        var user = TestBuilders.User(TestBuilders.UserGroup(
            permissions: [Read],
            granularPermissions: [
                TestBuilders.PropertyValueGranular(prop.Key, $"{prop.Key:D}|"),
            ]));
        Assert.False(PropertyAuditService.CanReadProperty(user, prop));
    }

    [Fact]
    public void GranularReadInOneGroup_WriteInAnother_UnionAllows()
    {
        var prop = TestBuilders.PropertyType("description").Object;
        var user = TestBuilders.User(
            TestBuilders.UserGroup(granularPermissions: [
                TestBuilders.PropertyValueGranular(prop.Key, $"{prop.Key:D}|{Read}"),
            ]),
            TestBuilders.UserGroup(granularPermissions: [
                TestBuilders.PropertyValueGranular(prop.Key, $"{prop.Key:D}|{Write}"),
            ]));
        Assert.True(PropertyAuditService.CanReadProperty(user, prop));
    }

    [Fact]
    public void GranularForOtherProperty_DoesNotOverride()
    {
        var prop = TestBuilders.PropertyType("description").Object;
        var otherKey = Guid.NewGuid();
        var user = TestBuilders.User(TestBuilders.UserGroup(
            permissions: [Read],
            granularPermissions: [
                TestBuilders.PropertyValueGranular(otherKey, $"{otherKey:D}|{Write}"),
            ]));
        Assert.True(PropertyAuditService.CanReadProperty(user, prop));
    }

    [Fact]
    public void ComposedProperty_KeyMatch_Applies()
    {
        // A shared (composition) property type uses its own key for granular matching.
        var sharedProp = TestBuilders.PropertyType("sharedDescription").Object;
        var user = TestBuilders.User(TestBuilders.UserGroup(granularPermissions: [
            TestBuilders.PropertyValueGranular(sharedProp.Key, $"{sharedProp.Key:D}|{Write}"),
        ]));
        Assert.False(PropertyAuditService.CanReadProperty(user, sharedProp));
    }

    [Fact]
    public async Task Catalog_OmitsUnreadableProperties()
    {
        var readableProp = TestBuilders.PropertyType("title");
        var hiddenProp = TestBuilders.PropertyType("secret");
        var fixture = new ServiceFixture
        {
            ContentTypes = [TestBuilders.ContentType(
                "article", 1,
                compositionProperties: [readableProp.Object, hiddenProp.Object]).Object],
            Languages = [],
        };
        var user = TestBuilders.User(TestBuilders.UserGroup(
            permissions: [Read],
            granularPermissions: [
                TestBuilders.PropertyValueGranular(hiddenProp.Object.Key, $"{hiddenProp.Object.Key:D}|{Write}"),
            ]));

        var result = await fixture.Create().GetCatalogAsync(user);

        Assert.True(result.Succeeded);
        var type = Assert.Single(result.Value!.DocumentTypes);
        var property = Assert.Single(type.Properties);
        Assert.Equal("title", property.Alias);
    }
}

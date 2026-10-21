using Moq;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Models.Membership.Permissions;

namespace PropertyAuditTool.Tests;

/// <summary>
/// Mock builders for Umbraco service/model interfaces used by the audit service.
/// </summary>
internal static class TestBuilders
{
    public static Mock<IPropertyType> PropertyType(
        string alias,
        Guid? key = null,
        ContentVariation variations = ContentVariation.Nothing,
        string? name = null,
        string editorAlias = "Umbraco.TextBox")
    {
        var mock = new Mock<IPropertyType>();
        mock.SetupGet(p => p.Alias).Returns(alias);
        mock.SetupGet(p => p.Key).Returns(key ?? Guid.NewGuid());
        mock.SetupGet(p => p.Variations).Returns(variations);
        mock.SetupGet(p => p.Name).Returns(name ?? alias);
        mock.SetupGet(p => p.PropertyEditorAlias).Returns(editorAlias);
        return mock;
    }

    public static Mock<IContentType> ContentType(
        string alias,
        int id,
        bool isElement = false,
        ContentVariation variations = ContentVariation.Nothing,
        params IPropertyType[] compositionProperties)
    {
        var mock = new Mock<IContentType>();
        mock.SetupGet(t => t.Alias).Returns(alias);
        mock.SetupGet(t => t.Name).Returns(alias);
        mock.SetupGet(t => t.Id).Returns(id);
        mock.SetupGet(t => t.IsElement).Returns(isElement);
        mock.SetupGet(t => t.Variations).Returns(variations);
        mock.SetupGet(t => t.CompositionPropertyTypes)
            .Returns(compositionProperties.ToList());
        return mock;
    }

    public static Mock<ISimpleContentType> SimpleContentType(
        string alias, int id = 0, ContentVariation variations = ContentVariation.Nothing)
    {
        var mock = new Mock<ISimpleContentType>();
        mock.SetupGet(t => t.Alias).Returns(alias);
        mock.SetupGet(t => t.Id).Returns(id);
        mock.SetupGet(t => t.Variations).Returns(variations);
        return mock;
    }

    public static Mock<IProperty> Property(IPropertyType propertyType)
    {
        var mock = new Mock<IProperty>();
        mock.SetupGet(p => p.Alias).Returns(propertyType.Alias);
        mock.SetupGet(p => p.PropertyType).Returns(propertyType);
        return mock;
    }

    /// <summary>
    /// An IContent node. <paramref name="propertyValue"/> is returned by GetValue;
    /// when <paramref name="hasProperty"/> is false the node does not define the alias.
    /// </summary>
    public static Mock<IContent> Content(
        int id,
        string propertyAlias,
        ISimpleContentType contentType,
        bool hasProperty = true,
        IProperty? property = null,
        object? propertyValue = null,
        bool trashed = false,
        string name = "node",
        Guid? key = null,
        string? cultureName = null,
        DateTime? updated = null)
    {
        var mock = new Mock<IContent>();
        mock.SetupGet(c => c.Id).Returns(id);
        mock.SetupGet(c => c.Key).Returns(key ?? Guid.NewGuid());
        mock.SetupGet(c => c.Trashed).Returns(trashed);
        mock.SetupGet(c => c.Name).Returns(name);
        mock.SetupGet(c => c.UpdateDate).Returns(updated ?? new DateTime(2026, 1, 1));
        mock.SetupGet(c => c.ContentType).Returns(contentType);
        mock.Setup(c => c.GetCultureName(It.IsAny<string>())).Returns(cultureName);

        var properties = new Mock<IPropertyCollection>();
        var outProperty = property;
        properties
            .Setup(p => p.TryGetValue(propertyAlias, out outProperty))
            .Returns(hasProperty);
        mock.SetupGet(c => c.Properties).Returns(properties.Object);

        mock.Setup(c => c.GetValue(
                propertyAlias,
                It.IsAny<string>(),
                It.IsAny<string>(),
                false))
            .Returns(propertyValue);

        return mock;
    }

    public static IReadOnlyUserGroup UserGroup(
        IEnumerable<string>? permissions = null,
        IEnumerable<IGranularPermission>? granularPermissions = null)
    {
        var mock = new Mock<IReadOnlyUserGroup>();
        mock.SetupGet(g => g.Permissions)
            .Returns(new HashSet<string>(permissions ?? []));
        mock.SetupGet(g => g.GranularPermissions)
            .Returns(new HashSet<IGranularPermission>(granularPermissions ?? []));
        return mock.Object;
    }

    public static DocumentPropertyValueGranularPermission PropertyValueGranular(
        Guid key, string permission) =>
        new() { Key = key, Permission = permission };

    public static IUser User(params IReadOnlyUserGroup[] groups)
    {
        var mock = new Mock<IUser>();
        mock.SetupGet(u => u.Groups).Returns(groups);
        return mock.Object;
    }

    /// <summary>Default user: allowed to read all property values (no granular overrides).</summary>
    public static IUser ReadableUser() =>
        User(UserGroup(permissions: ["Umb.Document.PropertyValue.Read"]));

    public static ILanguage Language(string isoCode, string? name = null, bool isDefault = false)
    {
        var mock = new Mock<ILanguage>();
        mock.SetupGet(l => l.IsoCode).Returns(isoCode);
        mock.SetupGet(l => l.CultureName).Returns(name ?? isoCode);
        mock.SetupGet(l => l.IsDefault).Returns(isDefault);
        return mock.Object;
    }
}

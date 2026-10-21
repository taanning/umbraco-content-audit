using Moq;
using PropertyAuditTool.Models;
using PropertyAuditTool.Persistence;
using PropertyAuditTool.Services;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Infrastructure.Scoping;
using Xunit;

namespace PropertyAuditTool.Tests;

public class SavedQueryServiceTests
{
    private static readonly Guid UserAKey = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserBKey = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly Mock<SavedQueryStore> _store =
        new(Mock.Of<IScopeProvider>());

    private SavedQueryService Service => new(_store.Object);

    private static IUser User(Guid key, string name = "User")
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.Key).Returns(key);
        user.SetupGet(u => u.Name).Returns(name);
        user.SetupGet(u => u.Username).Returns("user");
        return user.Object;
    }

    private static SavedQueryDefinition Definition() => new()
    {
        DocumentTypeAlias = null,
        PropertyAlias = "description",
        Operator = "isEmpty",
        SearchValue = null,
        Culture = null,
    };

    private static SavedQueryWriteRequest Write(
        string name = "Missing descriptions",
        bool shared = false,
        int? version = null) => new()
    {
        Name = name,
        IsShared = shared,
        Query = Definition(),
        Version = version,
    };

    private static SavedQueryRecord Record(
        Guid ownerKey,
        string name = "q",
        bool shared = false,
        int version = 3,
        string definition = """{"propertyAlias":"description","operator":"isEmpty"}""") => new()
    {
        Key = Guid.NewGuid(),
        OwnerKey = ownerKey,
        Name = name,
        OwnerName = "Owner",
        IsShared = shared,
        Definition = definition,
        CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        UpdatedUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified),
        Version = version,
    };

    // ---- visibility / ownership --------------------------------------------

    [Fact]
    public void List_QueriesStoreWithCurrentUserKey_AndFlagsOwnership()
    {
        var a = UserAKey;
        var mine = Record(a, name: "mine", shared: false);
        var sharedByOther = Record(UserBKey, name: "shared", shared: true);
        _store.Setup(s => s.ListVisible(a)).Returns([mine, sharedByOther]);

        var result = Service.List(User(a)).Items;

        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsOwner);
        Assert.False(result[1].IsOwner);
        Assert.Equal("shared", result[1].Name);
    }

    [Fact]
    public void Get_PrivateRecordOfOtherUser_StoreReturnsNull_ResultNotFound()
    {
        _store.Setup(s => s.GetVisible(It.IsAny<Guid>(), UserBKey))
            .Returns((SavedQueryRecord?)null);

        var result = Service.Get(User(UserBKey), Guid.NewGuid());

        Assert.Same(SavedQueryService.SavedQueryGetResult.NotFound, result);
    }

    [Fact]
    public void Get_SharedRecord_IsReadableByNonOwner()
    {
        var shared = Record(UserAKey, shared: true);
        _store.Setup(s => s.GetVisible(shared.Key, UserBKey)).Returns(shared);

        var result = Service.Get(User(UserBKey), shared.Key);

        Assert.NotNull(result.Response);
        Assert.False(result.Response.IsOwner);
        Assert.Equal("isEmpty", result.Response.Query.Operator);
    }

    [Fact]
    public void Create_AssignsCurrentUserAsOwner_VersionOne_UtcDates()
    {
        SavedQueryRecord? saved = null;
        _store.Setup(s => s.Create(It.IsAny<SavedQueryRecord>()))
            .Callback<SavedQueryRecord>(r => saved = r);

        var result = Service.Create(User(UserAKey, "Alice"), Write(shared: true));

        Assert.NotNull(result.Response);
        Assert.Equal(UserAKey, saved!.OwnerKey);
        Assert.Equal("Alice", saved.OwnerName);
        Assert.True(saved.IsShared);
        Assert.Equal(1, saved.Version);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedUtc.Kind);
        Assert.Equal(saved.CreatedUtc, saved.UpdatedUtc);
        Assert.Contains("\"propertyAlias\":\"description\"", saved.Definition);
    }

    [Fact]
    public void Response_UpdatedDate_IsUtcKind_EvenWhenDbUnspecified()
    {
        var record = Record(UserAKey);
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);

        var result = Service.Get(User(UserAKey), record.Key);

        Assert.Equal(DateTimeKind.Utc, result.Response!.UpdatedDate.Kind);
    }

    // ---- update / delete -----------------------------------------------------

    [Fact]
    public void Update_NotOwnedByUser_StoreNotFound_ReturnsNotFound()
    {
        _store.Setup(s => s.UpdateOwned(
                It.IsAny<Guid>(), UserBKey, It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>()))
            .Returns(SavedQueryStore.UpdateOutcome.NotFound);

        var result = Service.Update(User(UserBKey), Guid.NewGuid(), Write(version: 2));

        Assert.Same(SavedQueryService.SavedQueryUpdateResult.NotFound, result);
    }

    [Fact]
    public void Update_VersionRace_ReturnsConflict_NoOverwrite()
    {
        _store.Setup(s => s.UpdateOwned(
                It.IsAny<Guid>(), UserAKey, It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), 2))
            .Returns(SavedQueryStore.UpdateOutcome.Conflict);

        var result = Service.Update(User(UserAKey), Guid.NewGuid(), Write(version: 2));

        Assert.Same(SavedQueryService.SavedQueryUpdateResult.Conflict, result);
    }

    [Fact]
    public void Update_Success_BumpsVersion()
    {
        var updated = Record(UserAKey, version: 3);
        _store.Setup(s => s.UpdateOwned(
                It.IsAny<Guid>(), UserAKey, "new name", true,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), 2))
            .Returns(new SavedQueryStore.UpdateOutcome(SavedQueryStore.UpdateStatus.Updated, updated));

        var result = Service.Update(User(UserAKey), Guid.NewGuid(), Write(name: "new name", shared: true, version: 2));

        Assert.NotNull(result.Response);
        Assert.Equal(3, result.Response.Version);
    }

    [Fact]
    public void Delete_Owner_Succeeds()
    {
        _store.Setup(s => s.DeleteOwned(It.IsAny<Guid>(), UserAKey, 4))
            .Returns(SavedQueryStore.DeleteOutcome.Deleted);

        var result = Service.Delete(User(UserAKey), Guid.NewGuid(), 4);

        Assert.Equal(SavedQueryService.SavedQueryDeleteResult.StatusKind.Deleted, result.Status);
    }

    [Fact]
    public void Delete_NonOwner_ReturnsNotFound()
    {
        _store.Setup(s => s.DeleteOwned(It.IsAny<Guid>(), UserBKey, 4))
            .Returns(SavedQueryStore.DeleteOutcome.NotFound);

        var result = Service.Delete(User(UserBKey), Guid.NewGuid(), 4);

        Assert.Equal(SavedQueryService.SavedQueryDeleteResult.StatusKind.NotFound, result.Status);
    }

    // ---- validation ----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_IsValidationError(string name)
    {
        var result = Service.Create(User(UserAKey), Write(name: name));
        Assert.NotNull(result.Errors);
        Assert.Contains("name", result.Errors!.Keys);
    }

    [Fact]
    public void Create_TooLongName_IsValidationError()
    {
        var result = Service.Create(User(UserAKey), Write(name: new string('x', 121)));
        Assert.Contains("name", result.Errors!.Keys);
    }

    [Fact]
    public void Create_UnknownOperator_IsValidationError()
    {
        var request = Write();
        request.Query.Operator = "IsEmpty"; // case-sensitive contract
        var result = Service.Create(User(UserAKey), request);
        Assert.Contains("operator", result.Errors!.Keys);
    }

    [Fact]
    public void Create_ContainsRequiresSearchValue()
    {
        var request = Write();
        request.Query.Operator = "contains";
        request.Query.SearchValue = "  ";
        var result = Service.Create(User(UserAKey), request);
        Assert.Contains("searchValue", result.Errors!.Keys);
    }

    [Fact]
    public void Create_PreservesSearchValueWhitespace()
    {
        SavedQueryRecord? saved = null;
        _store.Setup(s => s.Create(It.IsAny<SavedQueryRecord>()))
            .Callback<SavedQueryRecord>(r => saved = r);

        var request = Write();
        request.Query.Operator = "contains";
        request.Query.SearchValue = "  padded  ";
        Service.Create(User(UserAKey), request);

        Assert.Contains("  padded  ", saved!.Definition);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void Update_InvalidVersion_IsValidationError(int? version)
    {
        var result = Service.Update(User(UserAKey), Guid.NewGuid(), Write(version: version));
        Assert.NotNull(result.Errors);
        Assert.Contains("version", result.Errors!.Keys);
        _store.Verify(s => s.UpdateOwned(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public void Delete_MissingVersion_IsValidationError()
    {
        var result = Service.Delete(User(UserAKey), Guid.NewGuid(), null);
        Assert.Equal(SavedQueryService.SavedQueryDeleteResult.StatusKind.Invalid, result.Status);
    }

    [Fact]
    public void Create_TooLongCulture_IsValidationError()
    {
        var request = Write();
        request.Query.Culture = new string('x', 21);
        var result = Service.Create(User(UserAKey), request);
        Assert.Contains("culture", result.Errors!.Keys);
    }

    [Fact]
    public void Serialization_IsStableCamelCase_WithoutPagingOrResults()
    {
        SavedQueryRecord? saved = null;
        _store.Setup(s => s.Create(It.IsAny<SavedQueryRecord>()))
            .Callback<SavedQueryRecord>(r => saved = r);

        Service.Create(User(UserAKey), Write());

        Assert.Contains("\"propertyAlias\"", saved!.Definition);
        Assert.Contains("\"operator\"", saved.Definition);
        Assert.DoesNotContain("skip", saved.Definition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("take", saved.Definition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("items", saved.Definition, StringComparison.OrdinalIgnoreCase);
    }
}

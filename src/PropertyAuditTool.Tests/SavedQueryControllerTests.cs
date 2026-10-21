using Microsoft.AspNetCore.Mvc;
using Moq;
using PropertyAuditTool.Controllers;
using PropertyAuditTool.Models;
using PropertyAuditTool.Persistence;
using PropertyAuditTool.Services;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Infrastructure.Scoping;
using Xunit;

namespace PropertyAuditTool.Tests;

public class SavedQueryControllerTests
{
    private static readonly Guid OwnerKey = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly Mock<SavedQueryStore> _store = new(Mock.Of<IScopeProvider>());
    private readonly SavedQueryApiController _controller;

    public SavedQueryControllerTests()
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.Key).Returns(OwnerKey);
        user.SetupGet(u => u.Name).Returns("Owner");

        var security = new Mock<IBackOfficeSecurity>();
        security.SetupGet(s => s.CurrentUser).Returns(user.Object);
        var accessor = new Mock<IBackOfficeSecurityAccessor>();
        accessor.SetupGet(a => a.BackOfficeSecurity).Returns(security.Object);

        _controller = new SavedQueryApiController(
            accessor.Object,
            new SavedQueryService(_store.Object));
    }

    private static SavedQueryWriteRequest Request() => new()
    {
        Name = "Missing descriptions",
        IsShared = false,
        Version = 2,
        Query = new SavedQueryDefinition
        {
            PropertyAlias = "description",
            Operator = "isEmpty",
        },
    };

    [Fact]
    public void Update_StoreConflict_Returns409()
    {
        _store.Setup(s => s.UpdateOwned(
                It.IsAny<Guid>(), OwnerKey, It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), 2))
            .Returns(SavedQueryStore.UpdateOutcome.Conflict);

        var result = _controller.UpdateSavedQuery(Guid.NewGuid(), Request());

        Assert.IsType<ConflictResult>(result);
    }

    [Fact]
    public void Update_AbsentOwnedRecord_Returns404()
    {
        _store.Setup(s => s.UpdateOwned(
                It.IsAny<Guid>(), OwnerKey, It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), 2))
            .Returns(SavedQueryStore.UpdateOutcome.NotFound);

        var result = _controller.UpdateSavedQuery(Guid.NewGuid(), Request());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void Delete_StoreConflict_Returns409()
    {
        _store.Setup(s => s.DeleteOwned(It.IsAny<Guid>(), OwnerKey, 2))
            .Returns(SavedQueryStore.DeleteOutcome.Conflict);

        var result = _controller.DeleteSavedQuery(Guid.NewGuid(), 2);

        Assert.IsType<ConflictResult>(result);
    }

    [Fact]
    public void Delete_AbsentOwnedRecord_Returns404()
    {
        _store.Setup(s => s.DeleteOwned(It.IsAny<Guid>(), OwnerKey, 2))
            .Returns(SavedQueryStore.DeleteOutcome.NotFound);

        var result = _controller.DeleteSavedQuery(Guid.NewGuid(), 2);

        Assert.IsType<NotFoundResult>(result);
    }
}

using Moq;
using PropertyAuditTool.Models;
using PropertyAuditTool.Persistence;
using PropertyAuditTool.Services;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Infrastructure.Scoping;
using Xunit;

namespace PropertyAuditTool.Tests;

public class SavedAuditServiceTests
{
    private static readonly Guid UserAKey = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserBKey = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly Mock<SavedQueryStore> _store =
        new(Mock.Of<IScopeProvider>());

    private static readonly System.Text.Json.JsonSerializerOptions Camel =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private SavedAuditService Service => new(_store.Object);

    private static IUser User(Guid key, string name = "User")
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.Key).Returns(key);
        user.SetupGet(u => u.Name).Returns(name);
        user.SetupGet(u => u.Username).Returns("user");
        return user.Object;
    }

    private const string LegacyJson =
        """{"documentTypeAlias":"newsPage","propertyAlias":"description","operator":"contains","searchValue":"foo","culture":"en-US"}""";

    private const string LegacyAllDocsJson =
        """{"documentTypeAlias":null,"propertyAlias":"description","operator":"isEmpty","searchValue":null,"culture":null}""";

    private const string V2Json =
        """{"schemaVersion":2,"allDocumentTypes":false,"types":[{"kind":"document","alias":"newsPage"}],"columns":[{"alias":"title","valueType":"text","filter":{"operator":"hasText"}}],"culture":null}""";

    private static AuditDefinition Definition(
        bool allDocs = false,
        string typeAlias = "newsPage",
        string typeKind = "document",
        string columnAlias = "title",
        string valueType = "text",
        AuditColumnFilter? filter = null) => new()
    {
        SchemaVersion = 2,
        AllDocumentTypes = allDocs,
        Types = allDocs ? new List<AuditTypeSelection>() : new List<AuditTypeSelection>
        {
            new() { Kind = typeKind, Alias = typeAlias },
        },
        Columns = new List<AuditColumnDefinition>
        {
            new() { Alias = columnAlias, ValueType = valueType, Filter = filter },
        },
        Culture = null,
    };

    private static AuditWriteRequest Write(
        string title = "Stale descriptions",
        bool shared = false,
        int? version = null,
        AuditDefinition? definition = null,
        string? description = null) => new()
    {
        Title = title,
        Description = description,
        IsShared = shared,
        Definition = definition ?? Definition(),
        Version = version,
    };

    private static SavedQueryRecord Record(
        Guid ownerKey,
        string name = "a",
        bool shared = false,
        int version = 3,
        string definition = V2Json) => new()
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

    // ---- definition detection / legacy adaptation -------------------------

    [Fact]
    public void IsVersion2Json_DetectsV2Only()
    {
        Assert.True(SavedAuditService.IsVersion2Json(V2Json));
        Assert.False(SavedAuditService.IsVersion2Json(LegacyJson));
        Assert.False(SavedAuditService.IsVersion2Json(LegacyAllDocsJson));
        Assert.False(SavedAuditService.IsVersion2Json("not json"));
        Assert.False(SavedAuditService.IsVersion2Json("{\"schemaVersion\":1}"));
    }

    [Fact]
    public void Get_LegacyV1Record_AdaptedToV2_PreservingFields()
    {
        var record = Record(UserAKey, definition: LegacyJson);
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);

        var result = Service.Get(User(UserAKey), record.Key);

        var definition = result.Response!.Definition;
        Assert.Equal(2, definition.SchemaVersion);
        Assert.False(definition.AllDocumentTypes);
        var type = Assert.Single(definition.Types);
        Assert.Equal("document", type.Kind);
        Assert.Equal("newsPage", type.Alias);
        var column = Assert.Single(definition.Columns);
        Assert.Equal("description", column.Alias);
        Assert.Equal("text", column.ValueType);
        Assert.Equal("contains", column.Filter!.Operator);
        Assert.Equal("foo", column.Filter.Value);
        Assert.Equal("en-US", definition.Culture);
    }

    [Fact]
    public void Get_LegacyV1AllDocuments_MapsToAllDocumentTypes()
    {
        var record = Record(UserAKey, definition: LegacyAllDocsJson);
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);

        var result = Service.Get(User(UserAKey), record.Key);

        var definition = result.Response!.Definition;
        Assert.True(definition.AllDocumentTypes);
        Assert.Empty(definition.Types);
        Assert.Equal("isEmpty", definition.Columns[0].Filter!.Operator);
    }

    [Fact]
    public void Get_LegacyRecord_DoesNotRewriteStoredDefinition()
    {
        var record = Record(UserAKey, definition: LegacyJson);
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);

        Service.Get(User(UserAKey), record.Key);

        Assert.Equal(LegacyJson, record.Definition);
        _store.Verify(s => s.UpdateAuditOwned(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<int>()), Times.Never);
    }

    // ---- visibility / ownership --------------------------------------------

    [Fact]
    public void List_JoinsOnlyCurrentViewerPreferences()
    {
        var mine = Record(UserAKey, name: "mine");
        var otherPref = new DashboardPreferenceRecord
        {
            Key = SavedQueryStore.DerivePreferenceKey(UserBKey, mine.Key),
            UserKey = UserBKey,
            AuditKey = mine.Key,
            IsPinned = true,
            Icon = "icon-star",
        };
        _store.Setup(s => s.ListVisible(UserAKey)).Returns([mine]);
        // The store only ever returns the caller's own preferences.
        _store.Setup(s => s.ListPreferences(UserAKey))
            .Returns(new List<DashboardPreferenceRecord>());

        var result = Service.List(User(UserAKey)).Items;

        Assert.Single(result);
        Assert.False(result[0].Dashboard.IsPinned);
    }

    [Fact]
    public void List_OwnPreference_IsJoinedToAudit()
    {
        var mine = Record(UserAKey, name: "mine");
        var pref = new DashboardPreferenceRecord
        {
            Key = SavedQueryStore.DerivePreferenceKey(UserAKey, mine.Key),
            UserKey = UserAKey,
            AuditKey = mine.Key,
            IsPinned = true,
            Icon = "icon-star",
            Colour = "#a1B2c3",
            LastCount = 12,
            LastDefinitionVersion = mine.Version,
        };
        _store.Setup(s => s.ListVisible(UserAKey)).Returns([mine]);
        _store.Setup(s => s.ListPreferences(UserAKey)).Returns([pref]);

        var result = Service.List(User(UserAKey)).Items;

        var dashboard = result[0].Dashboard;
        Assert.True(dashboard.IsPinned);
        Assert.Equal("icon-star", dashboard.Icon);
        Assert.Equal("#a1B2c3", dashboard.Colour);
        Assert.Equal(12, dashboard.LastCount);
        Assert.False(dashboard.IsStale);
    }

    [Fact]
    public void Get_PrivateAuditOfOtherUser_NotFound()
    {
        _store.Setup(s => s.GetVisible(It.IsAny<Guid>(), UserBKey))
            .Returns((SavedQueryRecord?)null);

        var result = Service.Get(User(UserBKey), Guid.NewGuid());

        Assert.Same(SavedAuditService.SavedAuditGetResult.NotFound, result);
    }

    [Fact]
    public void Update_OtherUsersAudit_ReturnsNotFound()
    {
        _store.Setup(s => s.UpdateAuditOwned(
                It.IsAny<Guid>(), UserBKey, It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<int>()))
            .Returns(SavedQueryStore.UpdateOutcome.NotFound);

        var result = Service.Update(User(UserBKey), Guid.NewGuid(), Write(version: 2));

        Assert.Same(SavedAuditService.SavedAuditUpdateResult.NotFound, result);
    }

    [Fact]
    public void Delete_OtherUsersAudit_ReturnsNotFound()
    {
        _store.Setup(s => s.DeleteOwned(It.IsAny<Guid>(), UserBKey, 4))
            .Returns(SavedQueryStore.DeleteOutcome.NotFound);

        var result = Service.Delete(User(UserBKey), Guid.NewGuid(), 4);

        Assert.Equal(SavedAuditService.SavedAuditDeleteResult.StatusKind.NotFound, result.Status);
    }

    // ---- create / update ------------------------------------------------------

    [Fact]
    public void Create_AssignsServerFields_VersionOne_UtcDates()
    {
        SavedQueryRecord? saved = null;
        _store.Setup(s => s.Create(It.IsAny<SavedQueryRecord>()))
            .Callback<SavedQueryRecord>(r => saved = r);

        var result = Service.Create(User(UserAKey, "Alice"),
            Write(shared: true, description: "Find missing copy"));

        Assert.NotNull(result.Response);
        Assert.Equal(UserAKey, saved!.OwnerKey);
        Assert.Equal("Alice", saved.OwnerName);
        Assert.Equal("Find missing copy", saved.Description);
        Assert.Equal(1, saved.Version);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedUtc.Kind);
        Assert.Contains("\"schemaVersion\":2", saved.Definition);
    }

    [Fact]
    public void Create_BlankDescription_StoresNull()
    {
        SavedQueryRecord? saved = null;
        _store.Setup(s => s.Create(It.IsAny<SavedQueryRecord>()))
            .Callback<SavedQueryRecord>(r => saved = r);

        Service.Create(User(UserAKey), Write(description: "   "));

        Assert.Null(saved!.Description);
    }

    [Fact]
    public void Update_LegacyOwnedRecord_UpgradesToV2_AndBumpsVersion()
    {
        var owned = Record(UserAKey, version: 4, definition: LegacyJson);
        owned.Name = "upgraded";
        owned.Version = 5;
        _store.Setup(s => s.UpdateAuditOwned(
                It.IsAny<Guid>(), UserAKey, It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), 4))
            .Returns(new SavedQueryStore.UpdateOutcome(SavedQueryStore.UpdateStatus.Updated, owned));

        var result = Service.Update(User(UserAKey), owned.Key, Write(version: 4));

        Assert.NotNull(result.Response);
        Assert.Equal(5, result.Response.Version);
        _store.Verify(s => s.UpdateAuditOwned(
            owned.Key, UserAKey, It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<bool>(), It.Is<string>(d => d.Contains("\"schemaVersion\":2")),
            It.IsAny<string>(), It.IsAny<DateTime>(), 4));
    }

    [Fact]
    public void Update_VersionRace_ReturnsConflict()
    {
        _store.Setup(s => s.UpdateAuditOwned(
                It.IsAny<Guid>(), UserAKey, It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), 2))
            .Returns(SavedQueryStore.UpdateOutcome.Conflict);

        var result = Service.Update(User(UserAKey), Guid.NewGuid(), Write(version: 2));

        Assert.Same(SavedAuditService.SavedAuditUpdateResult.Conflict, result);
    }

    [Fact]
    public void Dashboard_StaleFlagWhenPreferenceVersionDiffers()
    {
        var record = Record(UserAKey, version: 5);
        var pref = new DashboardPreferenceRecord
        {
            Key = SavedQueryStore.DerivePreferenceKey(UserAKey, record.Key),
            UserKey = UserAKey,
            AuditKey = record.Key,
            IsPinned = true,
            Icon = "icon-search",
            LastCount = 7,
            LastDefinitionVersion = 4,
        };
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);
        _store.Setup(s => s.GetPreference(
                SavedQueryStore.DerivePreferenceKey(UserAKey, record.Key), UserAKey, record.Key))
            .Returns(pref);

        var result = Service.Get(User(UserAKey), record.Key);

        Assert.True(result.Response!.Dashboard.IsStale);
        Assert.Equal(7, result.Response.Dashboard.LastCount);
    }

    [Fact]
    public void Dashboard_DefaultsWhenNoPreference_NoCount()
    {
        var record = Record(UserAKey);
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);
        _store.Setup(s => s.GetPreference(It.IsAny<Guid>(), UserAKey, record.Key))
            .Returns((DashboardPreferenceRecord?)null);

        var result = Service.Get(User(UserAKey), record.Key);

        var dashboard = result.Response!.Dashboard;
        Assert.False(dashboard.IsPinned);
        Assert.Equal(SavedAuditService.DefaultIcon, dashboard.Icon);
        Assert.Null(dashboard.LastCount);
        Assert.False(dashboard.IsStale);
    }

    // ---- validation ------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankTitle_IsValidationError(string title)
    {
        var result = Service.Create(User(UserAKey), Write(title: title));
        Assert.NotNull(result.Errors);
        Assert.Contains("title", result.Errors!.Keys);
    }

    [Fact]
    public void Create_TooLongDescription_IsValidationError()
    {
        var result = Service.Create(User(UserAKey), Write(description: new string('x', 2001)));
        Assert.Contains("description", result.Errors!.Keys);
    }

    [Fact]
    public void Create_NoTypesAndNotAllDocuments_IsValidationError()
    {
        var definition = Definition();
        definition.Types = new List<AuditTypeSelection>();
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Contains("definition.types", result.Errors!.Keys);
    }

    [Fact]
    public void Create_AllDocumentTypes_RequiresNoTypes()
    {
        var result = Service.Create(User(UserAKey), Write(definition: Definition(allDocs: true)));
        Assert.Null(result.Errors);
    }

    [Fact]
    public void Create_DuplicateTypeSelection_RetainsFirst_NotError()
    {
        SavedQueryRecord? saved = null;
        _store.Setup(s => s.Create(It.IsAny<SavedQueryRecord>()))
            .Callback<SavedQueryRecord>(r => saved = r);

        var definition = Definition();
        definition.Types.Add(new AuditTypeSelection { Kind = "document", Alias = "newsPage" });
        definition.Types.Add(new AuditTypeSelection { Kind = "element", Alias = "newsPage" });

        var result = Service.Create(User(UserAKey), Write(definition: definition));

        Assert.Null(result.Errors);
        var types = System.Text.Json.JsonSerializer.Deserialize<AuditDefinition>(saved!.Definition, Camel)!.Types;
        Assert.Equal(2, types.Count);
        Assert.Equal("document", types[0].Kind);
        Assert.Equal("element", types[1].Kind);
    }

    [Fact]
    public void Create_DuplicateColumnAlias_RetainsFirst_NotError()
    {
        SavedQueryRecord? saved = null;
        _store.Setup(s => s.Create(It.IsAny<SavedQueryRecord>()))
            .Callback<SavedQueryRecord>(r => saved = r);

        var definition = Definition();
        definition.Columns.Add(new AuditColumnDefinition { Alias = "title", ValueType = "number" });

        var result = Service.Create(User(UserAKey), Write(definition: definition));

        Assert.Null(result.Errors);
        var columns = System.Text.Json.JsonSerializer.Deserialize<AuditDefinition>(saved!.Definition, Camel)!.Columns;
        Assert.Single(columns);
        Assert.Equal("text", columns[0].ValueType);
    }

    [Fact]
    public void Create_WhitespaceAliases_AreTrimmedInStoredDefinition()
    {
        SavedQueryRecord? saved = null;
        _store.Setup(s => s.Create(It.IsAny<SavedQueryRecord>()))
            .Callback<SavedQueryRecord>(r => saved = r);

        var definition = Definition(typeAlias: "  newsPage ", columnAlias: " title ");
        definition.Culture = "   ";

        var result = Service.Create(User(UserAKey), Write(definition: definition));

        Assert.Null(result.Errors);
        var stored = System.Text.Json.JsonSerializer.Deserialize<AuditDefinition>(saved!.Definition, Camel)!;
        Assert.Equal("newsPage", stored.Types[0].Alias);
        Assert.Equal("title", stored.Columns[0].Alias);
        Assert.Null(stored.Culture);
    }

    [Fact]
    public void Create_NullTypeAndColumnEntries_AreStructured400_NotNre()
    {
        var definition = Definition();
        definition.Types.Add(null!);
        definition.Columns.Add(null!);

        var result = Service.Create(User(UserAKey), Write(definition: definition));

        Assert.NotNull(result.Errors);
        Assert.Contains("definition.types[1]", result.Errors!.Keys);
        Assert.Contains("definition.columns[1]", result.Errors.Keys);
    }

    [Fact]
    public void Create_MalformedExpression_IsValidationError()
    {
        var definition = Definition(
            filter: new AuditColumnFilter { Operator = "equals", Expression = "1 = 1; DROP TABLE" });
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Contains("definition.columns[0].filter", result.Errors!.Keys);
    }

    [Fact]
    public void Create_NumericOperandWithComma_IsValidationError()
    {
        var definition = Definition(valueType: "number",
            filter: new AuditColumnFilter { Operator = "greaterThan", Value = "1,5" });
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Contains("definition.columns[0].filter", result.Errors!.Keys);
    }

    [Fact]
    public void Create_BadKind_IsValidationError()
    {
        var result = Service.Create(User(UserAKey), Write(definition: Definition(typeKind: "media")));
        Assert.Contains("definition.types[0].kind", result.Errors!.Keys);
    }

    [Fact]
    public void Create_ContainsOnNumberColumn_IsValidationError()
    {
        var definition = Definition(valueType: "number",
            filter: new AuditColumnFilter { Operator = "contains", Value = "1" });
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Contains("definition.columns[0].filter", result.Errors!.Keys);
    }

    [Fact]
    public void Create_OrderingOnTextColumn_IsValidationError()
    {
        var definition = Definition(valueType: "text",
            filter: new AuditColumnFilter { Operator = "greaterThan", Value = "5" });
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Contains("definition.columns[0].filter", result.Errors!.Keys);
    }

    [Fact]
    public void Create_ContainsOnBooleanColumn_IsValidationError()
    {
        var definition = Definition(valueType: "boolean",
            filter: new AuditColumnFilter { Operator = "contains", Value = "x" });
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Contains("definition.columns[0].filter", result.Errors!.Keys);
    }

    [Fact]
    public void Create_EqualsWithoutValue_IsValidationError()
    {
        var definition = Definition(
            filter: new AuditColumnFilter { Operator = "equals", Value = "  " });
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Contains("definition.columns[0].filter", result.Errors!.Keys);
    }

    [Fact]
    public void Create_IsEmptyNeedsNoValue()
    {
        var result = Service.Create(User(UserAKey), Write(
            definition: Definition(filter: new AuditColumnFilter { Operator = "isEmpty" })));
        Assert.Null(result.Errors);
    }

    [Fact]
    public void Create_ExpressionCountsAsValue()
    {
        var definition = Definition(
            filter: new AuditColumnFilter { Operator = "equals", Expression = "='x'" });
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Null(result.Errors);
    }

    [Fact]
    public void Create_WrongSchemaVersion_IsValidationError()
    {
        var definition = Definition();
        definition.SchemaVersion = 1;
        var result = Service.Create(User(UserAKey), Write(definition: definition));
        Assert.Contains("definition.schemaVersion", result.Errors!.Keys);
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
    }

    // ---- dashboard preferences ---------------------------------------------------

    [Fact]
    public void SetDashboardPreference_NonOwnerOnSharedAudit_Allowed()
    {
        var shared = Record(UserAKey, shared: true);
        var pref = new DashboardPreferenceRecord
        {
            Key = SavedQueryStore.DerivePreferenceKey(UserBKey, shared.Key),
            UserKey = UserBKey,
            AuditKey = shared.Key,
            IsPinned = true,
            Icon = "icon-star",
        };
        _store.Setup(s => s.GetVisible(shared.Key, UserBKey)).Returns(shared);
        _store.Setup(s => s.SetPreference(UserBKey, shared.Key, true, "icon-star",
                It.Is<string?>(colour => colour != null && SavedAuditService.ThemeColours.Contains(colour))))
            .Returns(pref);

        var result = Service.SetDashboardPreference(User(UserBKey), shared.Key,
            new AuditDashboardPreferenceWriteRequest { IsPinned = true, Icon = "icon-star" });

        Assert.NotNull(result.Response);
        Assert.False(result.Response.IsOwner);
        Assert.True(result.Response.Dashboard.IsPinned);
    }

    [Fact]
    public void SetDashboardPreference_InvisibleAudit_NotFound()
    {
        _store.Setup(s => s.GetVisible(It.IsAny<Guid>(), UserBKey))
            .Returns((SavedQueryRecord?)null);

        var result = Service.SetDashboardPreference(User(UserBKey), Guid.NewGuid(),
            new AuditDashboardPreferenceWriteRequest { IsPinned = true });

        Assert.Same(SavedAuditService.SavedAuditPreferenceResult.NotFound, result);
        _store.Verify(s => s.SetPreference(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void SetDashboardPreference_DefaultIconApplied()
    {
        var record = Record(UserAKey);
        DashboardPreferenceRecord? written = null;
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);
        _store.Setup(s => s.SetPreference(
                UserAKey, record.Key, false, SavedAuditService.DefaultIcon, null))
            .Returns((Guid u, Guid a, bool p, string i, string? c) => written =
                new DashboardPreferenceRecord
                {
                    Key = SavedQueryStore.DerivePreferenceKey(u, a),
                    UserKey = u,
                    AuditKey = a,
                    IsPinned = p,
                    Icon = i,
                    Colour = c,
                });

        var result = Service.SetDashboardPreference(User(UserAKey), record.Key,
            new AuditDashboardPreferenceWriteRequest());

        Assert.Equal(SavedAuditService.DefaultIcon, written!.Icon);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("ICON-star")]
    [InlineData("icon star")]
    public void SetDashboardPreference_BadIcon_IsValidationError(string icon)
    {
        var record = Record(UserAKey);
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);

        var result = Service.SetDashboardPreference(User(UserAKey), record.Key,
            new AuditDashboardPreferenceWriteRequest { Icon = icon });

        Assert.Equal(SavedAuditService.SavedAuditPreferenceResult.StatusKind.Invalid, result.Status);
        Assert.Contains("icon", result.Errors!.Keys);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#fff")]
    [InlineData("#GGGGGG")]
    public void SetDashboardPreference_BadColour_IsValidationError(string colour)
    {
        var record = Record(UserAKey);
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);

        var result = Service.SetDashboardPreference(User(UserAKey), record.Key,
            new AuditDashboardPreferenceWriteRequest { Colour = colour });

        Assert.Equal(SavedAuditService.SavedAuditPreferenceResult.StatusKind.Invalid, result.Status);
        Assert.Contains("colour", result.Errors!.Keys);
    }

    // ---- legacy API cannot downgrade v2 ---------------------------------------

    [Fact]
    public void LegacyGet_V2Record_ReturnsNotFound()
    {
        var record = Record(UserAKey, definition: V2Json);
        _store.Setup(s => s.GetVisible(record.Key, UserAKey)).Returns(record);

        var result = new SavedQueryService(_store.Object).Get(User(UserAKey), record.Key);

        Assert.Same(SavedQueryService.SavedQueryGetResult.NotFound, result);
    }

    [Fact]
    public void LegacyList_OmitsV2Records()
    {
        var v2 = Record(UserAKey, name: "v2", definition: V2Json);
        var v1 = Record(UserAKey, name: "v1", definition: LegacyJson);
        _store.Setup(s => s.ListVisible(UserAKey)).Returns([v2, v1]);

        var result = new SavedQueryService(_store.Object).List(User(UserAKey)).Items;

        Assert.Single(result);
        Assert.Equal("v1", result[0].Name);
    }

    [Fact]
    public void LegacyUpdate_OwnedV2Record_ReturnsConflict_NoDowngrade()
    {
        var record = Record(UserAKey, definition: V2Json);
        _store.Setup(s => s.GetOwned(record.Key, UserAKey)).Returns(record);

        var request = new SavedQueryWriteRequest
        {
            Name = "legacy",
            Query = new SavedQueryDefinition
            {
                PropertyAlias = "description",
                Operator = "isEmpty",
            },
            Version = record.Version,
        };
        var result = new SavedQueryService(_store.Object).Update(User(UserAKey), record.Key, request);

        Assert.Same(SavedQueryService.SavedQueryUpdateResult.Conflict, result);
        _store.Verify(s => s.UpdateOwned(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public void DerivePreferenceKey_IsDeterministicAndUserScoped()
    {
        var auditKey = Guid.NewGuid();
        var a1 = SavedQueryStore.DerivePreferenceKey(UserAKey, auditKey);
        var a2 = SavedQueryStore.DerivePreferenceKey(UserAKey, auditKey);
        var b = SavedQueryStore.DerivePreferenceKey(UserBKey, auditKey);

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void TableDefinition_NormalizeAndRead_PreservesNewScopeWithoutRewritingLegacy()
    {
        var definition = Definition();
        definition.SchemaVersion = 3;
        definition.AllElementTypes = true;
        definition.AllCultures = false;
        definition.Cultures = ["", " en-US ", "EN-US"];
        definition.RowFilters = [new() { Alias = " content ", ValueType = "text",
            Filter = new() { Operator = "contains", Value = "keep" } }];
        definition.HiddenBaseColumns = [" location "];
        var normalized = SavedAuditService.NormalizeDefinition(definition);
        Assert.Empty(SavedAuditService.ValidateDefinition(normalized));
        var restored = SavedAuditService.DeserializeDefinition(
            System.Text.Json.JsonSerializer.Serialize(normalized, Camel));
        Assert.Equal(3, restored.SchemaVersion);
        Assert.True(restored.AllElementTypes);
        Assert.Equal(new[] { "", "en-US" }, restored.Cultures);
        Assert.Equal("content", Assert.Single(restored.RowFilters).Alias);
        Assert.Equal("location", Assert.Single(restored.HiddenBaseColumns));
        Assert.Null(SavedAuditService.DeserializeDefinition(V2Json).Cultures);
        Assert.False(SavedAuditService.DeserializeDefinition(V2Json).AllElementTypes);
    }

    [Fact]
    public void TableDefinition_AllowsMoreColumnsAndEmptyScope_ButLegacyLimitsRemain()
    {
        var definition = Definition();
        definition.Columns = Enumerable.Range(0, 40)
            .Select(index => new AuditColumnDefinition { Alias = $"field{index}", ValueType = "text" }).ToList();
        Assert.NotEmpty(SavedAuditService.ValidateDefinition(definition));
        definition.SchemaVersion = 3;
        Assert.Empty(SavedAuditService.ValidateDefinition(definition));
        definition.Types.Clear();
        definition.Columns.Clear();
        Assert.Empty(SavedAuditService.ValidateDefinition(definition));
    }

    [Fact]
    public void PinningWithoutAColour_PreservesPreviousPersonalColour()
    {
        Assert.All(SavedAuditService.ThemeColours, colour => Assert.True(colour.Length <= 7));
        var record = Record(UserAKey);
        _store.Setup(store => store.GetVisible(record.Key, UserAKey)).Returns(record);
        _store.Setup(store => store.GetPreference(It.IsAny<Guid>(), UserAKey, record.Key))
            .Returns(new DashboardPreferenceRecord { Colour = "#123456", Icon = "icon-search" });
        Service.SetDashboardPreference(User(UserAKey), record.Key,
            new AuditDashboardPreferenceWriteRequest { IsPinned = true });
        _store.Verify(store => store.SetPreference(UserAKey, record.Key, true, "icon-search", "#123456"), Times.Once);
    }
}

using PropertyAuditTool.Models;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Hosting;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace PropertyAuditTool.Services;

/// <summary>
/// Viewer-owned incremental audit runs. Advances the raw candidate cursor in
/// bounded requests using the lead-authored cursor/query recipe; retains only
/// matching rows in memory and never writes content or results to the
/// database. Authorization, visibility and schema safety are re-validated for
/// the current viewer on every call.
/// </summary>
public sealed class AuditExecutionService
{
    public const int PageSize = 250;
    public const int AdvanceMaxRaw = 2000;
    public const int RunMaxRaw = 100000;
    public const int MaxRetainedRows = 25000;

    /// <summary>Lead-authored per-run bound on summed retained raw text length.</summary>
    public const long MaxRetainedCharacters = 4_000_000;
    public const long MaxRetainedCells = 400_000;
    public const int MaxNestDepth = 16;
    public const int MaxDisplayValueLength = 2000;
    public const int MinTake = 1;
    public const int MaxTake = 200;
    public const int DefaultTake = 50;

    private static readonly ISet<string> BlockEditorAliases = new HashSet<string>(StringComparer.Ordinal)
    {
        "Umbraco.BlockList", "Umbraco.BlockGrid",
    };

    private readonly AuditRunStore _store;
    private readonly IContentService _contentService;
    private readonly IContentTypeService _contentTypeService;
    private readonly ILanguageService _languageService;
    private readonly IContentPermissionAuthorizer _contentPermissionAuthorizer;
    private readonly IHostingEnvironment _hostingEnvironment;
    private readonly SavedQueryStore _savedQueryStore;

    public AuditExecutionService(
        AuditRunStore store,
        IContentService contentService,
        IContentTypeService contentTypeService,
        ILanguageService languageService,
        IContentPermissionAuthorizer contentPermissionAuthorizer,
        IHostingEnvironment hostingEnvironment,
        SavedQueryStore savedQueryStore)
    {
        _store = store;
        _contentService = contentService;
        _contentTypeService = contentTypeService;
        _languageService = languageService;
        _contentPermissionAuthorizer = contentPermissionAuthorizer;
        _hostingEnvironment = hostingEnvironment;
        _savedQueryStore = savedQueryStore;
    }

    // ---- resolved plan ---------------------------------------------------------

    /// <summary>Per-call resolved metadata; never stored on the run.</summary>
    private sealed record ResolvedPlan
    {
        public required List<IContentType> OwnerTypes { get; init; }
        public required List<IContentType> DocumentTypes { get; init; }
        public required List<IContentType> ElementTypes { get; init; }
        public required IReadOnlyDictionary<Guid, IContentType> AllElementTypes { get; init; }
        public required bool ElementsSelected { get; init; }
        public required List<ResolvedColumn> Columns { get; init; }
        public string? Culture { get; init; }
        public IReadOnlyList<string?> Cultures { get; init; } = [];
        public bool MultiCulture { get; init; }
        public IReadOnlyDictionary<string, AuditCompiledFilter> RowFilters { get; init; }
            = new Dictionary<string, AuditCompiledFilter>();
        /// <summary>Ordered column-filter chain; folded left to right.</summary>
        public required List<ResolvedLink> FilterChain { get; init; }
    }

    private sealed class ResolvedColumn
    {
        public required string Alias { get; init; }
        public required string ValueType { get; init; }
        public AuditCompiledFilter? Filter { get; init; }
    }

    private sealed class ResolvedLink
    {
        public required ResolvedColumn Column { get; init; }
        /// <summary>"and" or "or" — connects this link to the next.</summary>
        public string? Joiner { get; init; }
    }

    private sealed class Resolution
    {
        public ResolvedPlan? Plan { get; init; }
        public Dictionary<string, string[]>? Errors { get; init; }
        public string? ForbiddenMessage { get; init; }
    }

    /// <summary>Resolves and validates definition metadata fresh for a call.</summary>
    private async Task<Resolution> ResolveAsync(IUser user, AuditDefinition definition)
    {
        var errors = SavedAuditService.ValidateDefinition(definition);
        if (errors.Count > 0)
        {
            return new Resolution { Errors = errors };
        }

        var allTypes = _contentTypeService.GetAll().ToArray();
        var elementTypes = allTypes.Where(type => type.IsElement).ToArray();
        var documentTypes = allTypes.Where(type => !type.IsElement).ToArray();

        var selectedDocuments = new List<IContentType>();
        var selectedElements = new List<IContentType>();
        foreach (var selection in definition.Types)
        {
            var match = allTypes.FirstOrDefault(type =>
                string.Equals(type.Alias, selection.Alias, StringComparison.Ordinal)
                && type.IsElement == string.Equals(selection.Kind, "element", StringComparison.Ordinal));
            if (match is null)
            {
                errors["definition.types"] = new[]
                {
                    $"Unknown {selection.Kind} type alias '{selection.Alias}'.",
                };
                return new Resolution { Errors = errors };
            }

            if (match.IsElement)
            {
                selectedElements.Add(match);
            }
            else if (!selectedDocuments.Any(type => type.Id == match.Id))
            {
                selectedDocuments.Add(match);
            }
        }

        if (definition.AllDocumentTypes)
        {
            foreach (var type in documentTypes)
            {
                if (!selectedDocuments.Any(selected => selected.Id == type.Id))
                {
                    selectedDocuments.Add(type);
                }
            }
        }
        if (definition.AllElementTypes)
        {
            foreach (var type in elementTypes)
                if (!selectedElements.Any(selected => selected.Id == type.Id)) selectedElements.Add(type);
        }

        var elementsSelected = selectedElements.Count > 0;
        var ownerTypes = elementsSelected ? documentTypes.ToList() : selectedDocuments;
        var rowTypes = selectedDocuments.Concat(selectedElements).ToArray();

        // Compile every active filter; every column must be defined and
        // readable on at least one selected row type.
        var columns = new List<ResolvedColumn>();
        foreach (var column in definition.Columns)
        {
            if (!AuditFilterCompiler.TryCompile(
                    column.ValueType,
                    column.Filter?.Operator ?? "hasText",
                    column.Filter?.Value,
                    column.Filter?.Expression,
                    out var compiled,
                    out var compileError))
            {
                errors[$"definition.columns"] = new[] { compileError ?? "Invalid filter." };
                return new Resolution { Errors = errors };
            }

            var definedOn = rowTypes
                .Select(type => type.CompositionPropertyTypes.FirstOrDefault(property =>
                    string.Equals(property.Alias, column.Alias, StringComparison.Ordinal)))
                .Where(property => property is not null)
                .ToArray();
            if (definedOn.Length == 0)
            {
                errors["definition.columns"] = new[]
                {
                    $"No selected type defines the property alias '{column.Alias}'.",
                };
                return new Resolution { Errors = errors };
            }

            if (definedOn.Any(property => property!.VariesBySegment()))
            {
                errors["definition.columns"] = new[]
                {
                    $"The property '{column.Alias}' varies by segment, which is not supported.",
                };
                return new Resolution { Errors = errors };
            }

            if (!definedOn.Any(property => PropertyAuditService.CanReadProperty(user, property!)))
            {
                return new Resolution
                {
                    ForbiddenMessage =
                        $"You do not have permission to read the property '{column.Alias}'.",
                };
            }

            columns.Add(new ResolvedColumn
            {
                Alias = column.Alias.Trim(),
                ValueType = column.ValueType,
                Filter = column.Filter is null ? null : compiled,
            });
        }

        // Culture requirement and normalization.
        var cultureRequired = rowTypes.Any(type => type.VariesByCulture())
            || columns.Any(column => rowTypes
                .Select(type => type.CompositionPropertyTypes.FirstOrDefault(property =>
                    string.Equals(property.Alias, column.Alias, StringComparison.Ordinal)))
                .Where(property => property is not null)
                .Any(property => property!.VariesByCulture()));
        if (!cultureRequired && elementsSelected)
        {
            cultureRequired = ownerTypes.Any(type => type.VariesByCulture()
                || type.CompositionPropertyTypes.Any(property =>
                    BlockEditorAliases.Contains(property.PropertyEditorAlias)
                    && PropertyAuditService.CanReadProperty(user, property)
                    && property.VariesByCulture()));
        }

        var languages = await _languageService.GetAllAsync();
        var multiCulture = definition.AllCultures || definition.Cultures is not null;
        var cultures = new List<string?>();
        string? normalizedCulture = null;
        if (multiCulture)
        {
            var requested = definition.AllCultures
                ? new[] { "" }.Concat(languages.Select(language => language.IsoCode))
                : definition.Cultures!;
            foreach (var requestedCulture in requested)
            {
                if (string.IsNullOrEmpty(requestedCulture))
                {
                    cultures.Add(null);
                    continue;
                }
                var culture = languages.FirstOrDefault(language => string.Equals(
                    language.IsoCode, requestedCulture, StringComparison.OrdinalIgnoreCase))?.IsoCode;
                if (culture is null)
                    return new Resolution { Errors = new() { ["definition.cultures"] = [$"Unknown culture '{requestedCulture}'."] } };
                if (await _contentPermissionAuthorizer.IsDeniedForCultures(user, new HashSet<string> { culture }))
                {
                    if (definition.AllCultures) continue;
                    return new Resolution { ForbiddenMessage = $"You do not have permission to read culture '{culture}'." };
                }
                cultures.Add(culture);
            }
        }
        else if (definition.Culture is not null)
        {
            normalizedCulture = languages
                .FirstOrDefault(language => string.Equals(
                    language.IsoCode, definition.Culture, StringComparison.OrdinalIgnoreCase))
                ?.IsoCode;
            if (normalizedCulture is null)
            {
                errors["definition.culture"] = new[] { $"Unknown culture '{definition.Culture}'." };
                return new Resolution { Errors = errors };
            }

            if (await _contentPermissionAuthorizer.IsDeniedForCultures(
                    user, new HashSet<string> { normalizedCulture }))
            {
                return new Resolution
                {
                    ForbiddenMessage = $"You do not have permission to read culture '{normalizedCulture}'.",
                };
            }
        }

        if (!multiCulture && cultureRequired && normalizedCulture is null)
        {
            errors["definition.culture"] = new[]
            {
                "A culture is required because a selected type or property varies by culture.",
            };
            return new Resolution { Errors = errors };
        }

        if (!multiCulture) cultures.Add(normalizedCulture);
        var rowFilters = new Dictionary<string, AuditCompiledFilter>();
        foreach (var column in definition.RowFilters.Where(column => column.Filter is not null))
        {
            var filter = column.Filter!;
            AuditFilterCompiler.TryCompile("text", filter.Operator, filter.Value, filter.Expression, out var compiled, out _);
            rowFilters[column.Alias] = compiled!;
        }

        // Ordered filter chain: declared links first, then any filtered column
        // missing from the declaration appended in column order.
        var chained = new HashSet<string>(StringComparer.Ordinal);
        var filterChain = new List<ResolvedLink>();
        foreach (var link in definition.FilterChain ?? [])
        {
            var column = columns.FirstOrDefault(candidate =>
                candidate.Filter is not null
                && string.Equals(candidate.Alias, link?.Column, StringComparison.Ordinal));
            if (column is not null && chained.Add(column.Alias))
            {
                filterChain.Add(new ResolvedLink { Column = column, Joiner = link!.Joiner });
            }
        }
        foreach (var column in columns.Where(column => column.Filter is not null))
        {
            if (chained.Add(column.Alias))
            {
                filterChain.Add(new ResolvedLink { Column = column });
            }
        }

        return new Resolution
        {
            Plan = new ResolvedPlan
            {
                OwnerTypes = ownerTypes,
                DocumentTypes = selectedDocuments,
                ElementTypes = selectedElements,
                AllElementTypes = elementTypes.ToDictionary(type => type.Key),
                ElementsSelected = elementsSelected,
                Columns = columns,
                Culture = normalizedCulture,
                Cultures = cultures.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                MultiCulture = multiCulture,
                RowFilters = rowFilters,
                FilterChain = filterChain,
            },
        };
    }

    // ---- public API ----------------------------------------------------------

    public async Task<RunResult<AuditRunProgress>> CreateAsync(
        IUser user, AuditRunCreateRequest request, CancellationToken cancellationToken)
    {
        AuditDefinition definition;
        Guid? savedAuditId = null;
        int? savedAuditVersion = null;

        if (request.AuditId is { } auditId)
        {
            var record = _savedQueryStore.GetVisible(auditId, user.Key);
            if (record is null)
            {
                return RunResult<AuditRunProgress>.NotFound;
            }

            definition = SavedAuditService.DeserializeDefinition(record.Definition);
            savedAuditId = record.Key;
            savedAuditVersion = record.Version;
        }
        else
        {
            if (request.Definition is null)
            {
                return RunResult<AuditRunProgress>.Invalid(
                    new Dictionary<string, string[]>
                    {
                        ["definition"] = new[] { "An audit definition is required." },
                    });
            }

            definition = SavedAuditService.NormalizeDefinition(request.Definition);
        }

        var resolution = await ResolveAsync(user, definition);
        if (resolution.Errors is not null)
        {
            return RunResult<AuditRunProgress>.Invalid(resolution.Errors);
        }
        if (resolution.ForbiddenMessage is not null)
        {
            return RunResult<AuditRunProgress>.Forbidden(resolution.ForbiddenMessage);
        }

        var utcNow = DateTime.UtcNow;
        var run = new AuditRunStore.AuditRunState
        {
            RunId = Guid.NewGuid(),
            OwnerKey = user.Key,
            Definition = definition,
            SavedAuditId = savedAuditId,
            SavedAuditVersion = savedAuditVersion,
            NormalizedCulture = resolution.Plan!.Culture,
            NormalizedCultures = resolution.Plan.MultiCulture ? resolution.Plan.Cultures : null,
            Status = "running",
            StartedUtc = utcNow,
            LastTouchedUtc = utcNow,
        };

        var outcome = _store.TryCreate(run, utcNow);
        if (outcome == AuditRunStore.CreateRunOutcome.TooManyForUser)
        {
            return RunResult<AuditRunProgress>.TooMany;
        }
        if (outcome == AuditRunStore.CreateRunOutcome.TooManyTotal)
        {
            return RunResult<AuditRunProgress>.Unavailable;
        }

        return RunResult<AuditRunProgress>.Ok(ToProgress(run));
    }

    public async Task<RunResult<AuditRunProgress>> AdvanceAsync(
        IUser user, Guid runId, CancellationToken cancellationToken)
    {
        var run = _store.GetForOwner(runId, user.Key, DateTime.UtcNow);
        if (run is null)
        {
            return RunResult<AuditRunProgress>.NotFound;
        }

        await run.Lock.WaitAsync(cancellationToken);
        try
        {
            var gate = await CheckAccessAsync(user, run, cancellationToken);
            if (gate is not null)
            {
                return gate;
            }

            if (run.Status == "running")
            {
                var resolution = await ResolveAsync(user, run.Definition);
                if (resolution.Errors is not null)
                {
                    return RunResult<AuditRunProgress>.Invalid(resolution.Errors);
                }
                if (resolution.ForbiddenMessage is not null)
                {
                    return RunResult<AuditRunProgress>.Forbidden(resolution.ForbiddenMessage);
                }

                await ScanBatchAsync(user, run, resolution.Plan!, cancellationToken);
            }

            run.LastTouchedUtc = DateTime.UtcNow;
            var accessible = await AccessibleRowsAsync(user, run, cancellationToken);
            var invalidation = await RevalidateRowsAsync<AuditRunProgress>(
                user, run, accessible, cancellationToken);
            if (invalidation is not null)
            {
                return invalidation;
            }

            if (run.Status == "completed" && run.SavedAuditId is { } auditId
                && !run.SummaryPersisted)
            {
                _savedQueryStore.SetCompletedSummary(
                    user.Key, auditId, accessible.Count, DateTime.UtcNow,
                    run.SavedAuditVersion ?? run.Definition.SchemaVersion);
                run.SummaryPersisted = true;
            }

            return RunResult<AuditRunProgress>.Ok(ToProgress(run, accessible));
        }
        finally
        {
            run.Lock.Release();
        }
    }

    public async Task<RunResult<AuditRunProgress>> GetProgressAsync(
        IUser user, Guid runId, CancellationToken cancellationToken)
    {
        var run = _store.GetForOwner(runId, user.Key, DateTime.UtcNow);
        if (run is null)
        {
            return RunResult<AuditRunProgress>.NotFound;
        }

        await run.Lock.WaitAsync(cancellationToken);
        try
        {
            var gate = await CheckAccessAsync(user, run, cancellationToken);
            if (gate is not null)
            {
                return gate;
            }

            run.LastTouchedUtc = DateTime.UtcNow;
            var accessible = await AccessibleRowsAsync(user, run, cancellationToken);
            var invalidation = await RevalidateRowsAsync<AuditRunProgress>(
                user, run, accessible, cancellationToken);
            if (invalidation is not null)
            {
                return invalidation;
            }

            return RunResult<AuditRunProgress>.Ok(ToProgress(run, accessible));
        }
        finally
        {
            run.Lock.Release();
        }
    }

    public async Task<RunResult<AuditRunResults>> GetResultsAsync(
        IUser user, Guid runId, int skip, int take, string? sortBy, string? direction,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (skip < 0)
        {
            errors["skip"] = new[] { "Skip must be 0 or greater." };
        }
        if (take is < MinTake or > MaxTake)
        {
            errors["take"] = new[] { $"Take must be between {MinTake} and {MaxTake}." };
        }
        var descending = direction == "desc";
        if (direction is not null and not ("asc" or "desc"))
        {
            errors["direction"] = new[] { "Direction must be 'asc' or 'desc'." };
        }
        if (errors.Count > 0)
        {
            return RunResult<AuditRunResults>.Invalid(errors);
        }

        var run = _store.GetForOwner(runId, user.Key, DateTime.UtcNow);
        if (run is null)
        {
            return RunResult<AuditRunResults>.NotFound;
        }

        var gate = await CheckAccessAsync<AuditRunResults>(user, run, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        await run.Lock.WaitAsync(cancellationToken);
        try
        {
            if (run.Status is not ("completed" or "partial" or "limited"))
            {
                return RunResult<AuditRunResults>.Invalid(
                    new Dictionary<string, string[]>
                    {
                        ["runId"] = new[] { "Results are only available after the run finishes." },
                    });
            }

            var sort = ResolveSort(sortBy, run.Definition);
            if (sort.Error is not null)
            {
                return RunResult<AuditRunResults>.Invalid(
                    new Dictionary<string, string[]> { ["sortBy"] = new[] { sort.Error } });
            }

            var accessible = await AccessibleRowsAsync(user, run, cancellationToken);
            var invalidation = await RevalidateRowsAsync<AuditRunResults>(
                user, run, accessible, cancellationToken);
            if (invalidation is not null)
            {
                return invalidation;
            }

            var sorted = accessible.OrderBy(row => row, new RetainedRowComparer(sort, descending)).ToArray();
            var items = sorted.Skip(skip).Take(take)
                .Select(ToRowDto)
                .ToArray();

            run.LastTouchedUtc = DateTime.UtcNow;
            return RunResult<AuditRunResults>.Ok(new AuditRunResults
            {
                Progress = ToProgress(run, accessible),
                Columns = run.Definition.Columns.Select(column => column.Alias.Trim()).ToArray(),
                Items = items,
                TotalItems = sorted.Length,
                Skip = skip,
                Take = take,
            });
        }
        finally
        {
            run.Lock.Release();
        }
    }

    public async Task<RunResult<object>> CancelAsync(
        IUser user, Guid runId, CancellationToken cancellationToken)
    {
        var run = _store.GetForOwner(runId, user.Key, DateTime.UtcNow);
        if (run is null)
        {
            return RunResult<object>.NotFound;
        }

        await run.Lock.WaitAsync(cancellationToken);
        try
        {
            run.Status = "cancelled";
            _store.Remove(runId);
            return RunResult<object>.Ok(new object());
        }
        finally
        {
            run.Lock.Release();
        }
    }

    // ---- access checks ---------------------------------------------------------

    /// <summary>Saved-audit visibility and culture re-checks for the viewer.</summary>
    private async Task<RunResult<T>?> CheckAccessAsync<T>(
        IUser user, AuditRunStore.AuditRunState run, CancellationToken cancellationToken)
    {
        if (run.SavedAuditId is { } auditId)
        {
            var record = _savedQueryStore.GetVisible(auditId, user.Key);
            if (record is null)
            {
                return RunResult<T>.NotFound;
            }

            if (run.SavedAuditVersion is not null && record.Version != run.SavedAuditVersion)
            {
                return RunResult<T>.Conflict(
                    "The saved audit changed since this run started; start a fresh run.");
            }
        }

        if (run.NormalizedCulture is not null
            && await _contentPermissionAuthorizer.IsDeniedForCultures(
                user, new HashSet<string> { run.NormalizedCulture }))
        {
            return RunResult<T>.Forbidden(
                $"You do not have permission to read culture '{run.NormalizedCulture}'.");
        }
        foreach (var culture in run.NormalizedCultures?.OfType<string>() ?? [])
        {
            if (await _contentPermissionAuthorizer.IsDeniedForCultures(user, new HashSet<string> { culture }))
                return RunResult<T>.Forbidden($"You do not have permission to read culture '{culture}'.");
        }

        return null;
    }

    private Task<RunResult<AuditRunProgress>?> CheckAccessAsync(
        IUser user, AuditRunStore.AuditRunState run, CancellationToken cancellationToken) =>
        CheckAccessAsync<AuditRunProgress>(user, run, cancellationToken);

    /// <summary>
    /// Re-authorizes every distinct retained owner document for the current
    /// viewer with the single-key authorizer; inaccessible rows are removed
    /// from this response/count only.
    /// </summary>
    private async Task<List<AuditRunStore.RetainedRow>> AccessibleRowsAsync(
        IUser user, AuditRunStore.AuditRunState run, CancellationToken cancellationToken)
    {
        var rows = run.Rows;
        var ownerKeys = rows.Select(row => row.OwnerDocumentKey).Distinct().ToArray();
        if (ownerKeys.Length == 0)
        {
            return rows;
        }

        var allowed = new HashSet<Guid>();
        foreach (var key in ownerKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _contentPermissionAuthorizer.IsDeniedAsync(
                    user, new[] { key }, new HashSet<string> { ActionBrowse.ActionLetter }))
            {
                allowed.Add(key);
            }
        }

        return rows.Where(row => allowed.Contains(row.OwnerDocumentKey)).ToList();
    }

    /// <summary>
    /// Verifies retained readable cells are still defined and readable under
    /// the current schema and viewer permissions. Missing metadata -> 409;
    /// lost property read access -> 403. Applied on every run call so counts
    /// and summaries never derive from cells the viewer can no longer read.
    /// </summary>
    private Task<RunResult<T>?> RevalidateRowsAsync<T>(
        IUser user,
        AuditRunStore.AuditRunState run,
        IReadOnlyList<AuditRunStore.RetainedRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return Task.FromResult<RunResult<T>?>(null);
        }

        var current = _contentTypeService.GetAll().ToDictionary(type => type.Key);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!current.TryGetValue(row.TypeKey, out var type))
            {
                return Task.FromResult<RunResult<T>?>(RunResult<T>.Conflict(
                    "A selected content type no longer exists; start a fresh run."));
            }

            // Ancestor block-property access path: a revoked or removed
            // wrapper must invalidate retained element rows even when the
            // element's own properties remain readable. Metadata only —
            // no stored values are parsed here.
            foreach (var access in row.TraversalAccess)
            {
                if (!current.TryGetValue(access.TypeKey, out var ancestorType))
                {
                    return Task.FromResult<RunResult<T>?>(RunResult<T>.Conflict(
                        "A block-traversal content type no longer exists; start a fresh run."));
                }

                var ancestorProperty = ancestorType.CompositionPropertyTypes
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Alias, access.Alias, StringComparison.Ordinal));
                if (ancestorProperty is null)
                {
                    return Task.FromResult<RunResult<T>?>(RunResult<T>.Conflict(
                        "A block-traversal property no longer exists; start a fresh run."));
                }

                if (!string.Equals(
                        ancestorProperty.PropertyEditorAlias,
                        access.EditorAlias,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult<RunResult<T>?>(RunResult<T>.Conflict(
                        "A block-traversal property changed editor; start a fresh run."));
                }

                if (ancestorProperty.VariesBySegment()
                    || (ancestorProperty.VariesByCulture()
                        && run.NormalizedCulture is null))
                {
                    return Task.FromResult<RunResult<T>?>(RunResult<T>.Conflict(
                        "A block-traversal property changed variation metadata; start a fresh run."));
                }

                if (!PropertyAuditService.CanReadProperty(user, ancestorProperty))
                {
                    return Task.FromResult<RunResult<T>?>(RunResult<T>.Forbidden(
                        $"You no longer have permission to read the property '{access.Alias}'."));
                }
            }

            foreach (var cell in row.Cells)
            {
                if (cell.Status is not ("filled" or "empty"))
                {
                    continue;
                }

                var property = type.CompositionPropertyTypes.FirstOrDefault(candidate =>
                    string.Equals(candidate.Alias, cell.Alias, StringComparison.Ordinal));
                if (property is null)
                {
                    return Task.FromResult<RunResult<T>?>(RunResult<T>.Conflict(
                        "A selected property no longer exists; start a fresh run."));
                }

                if (!PropertyAuditService.CanReadProperty(user, property))
                {
                    return Task.FromResult<RunResult<T>?>(RunResult<T>.Forbidden(
                        $"You no longer have permission to read the property '{cell.Alias}'."));
                }
            }
        }

        return Task.FromResult<RunResult<T>?>(null);
    }

    // ---- scanning ---------------------------------------------------------------

    /// <summary>Lead-authored cursor/page recipe; bounded per call.</summary>
    private async Task ScanBatchAsync(
        IUser user,
        AuditRunStore.AuditRunState run,
        ResolvedPlan plan,
        CancellationToken cancellationToken)
    {
        var ownerTypeIds = plan.OwnerTypes.Select(type => type.Id).ToArray();
        if (ownerTypeIds.Length == 0 || plan.Cultures.Count == 0)
        {
            run.Status = "completed";
            run.CompletedUtc = DateTime.UtcNow;
            return;
        }
        var visitedThisAdvance = 0L;
        var exhausted = false;
        var totalRecords = 0L;

        while (!exhausted
            && visitedThisAdvance < AdvanceMaxRaw
            && run.RawVisited < RunMaxRaw
            && !run.RetentionExceeded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageIndex = run.Cursor / PageSize;
            var withinPage = (int)(run.Cursor % PageSize);

            var page = _contentService.GetPagedOfTypes(
                ownerTypeIds,
                pageIndex,
                PageSize,
                out totalRecords,
                filter: null,
                ordering: Ordering.By("Id")).ToArray();

            if (run.Cursor >= totalRecords || page.Length <= withinPage)
            {
                exhausted = true;
                break;
            }

            for (var index = withinPage;
                index < page.Length
                    && visitedThisAdvance < AdvanceMaxRaw
                    && run.RawVisited < RunMaxRaw;
                index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var node = page[index];
                run.Cursor++;
                run.RawVisited++;
                visitedThisAdvance++;

                if (node.Trashed)
                {
                    continue;
                }

                // Single-key authorization per owner document.
                if (await _contentPermissionAuthorizer.IsDeniedAsync(
                        user, new[] { node.Key },
                        new HashSet<string> { ActionBrowse.ActionLetter }))
                {
                    continue;
                }

                run.ScannedCount++;
                foreach (var culture in run.NormalizedCultures ?? plan.Cultures)
                {
                    if (run.RetentionExceeded) break;
                    InspectDocument(user, run, plan with { Culture = culture }, node);
                }
            }

            // Exact exhaustion: the cursor covered every known candidate.
            exhausted = run.Cursor >= totalRecords;
        }

        if (!exhausted)
        {
            if (run.RawVisited >= RunMaxRaw || run.RetentionExceeded)
            {
                run.AddWarning(
                    run.RawVisited >= RunMaxRaw
                        ? $"The run reached the {RunMaxRaw} raw-candidate limit before completion."
                        : "The run reached a retained-data limit before completion.");
                run.Status = "limited";
                run.CompletedUtc = DateTime.UtcNow;
            }
            return;
        }

        run.CompletedUtc = DateTime.UtcNow;
        // A matching row that could not be retained means the snapshot is not
        // exact even when the candidate set was fully scanned.
        run.Status = run.RetentionExceeded
            ? "limited"
            : (run.HasPartialWarnings ? "partial" : "completed");
        if (run.RetentionExceeded)
        {
            run.AddWarning(
                "The run reached a retained-data limit before completion.");
        }
    }

    /// <summary>Produces document and/or element rows for one authorized node.</summary>
    private void InspectDocument(
        IUser user,
        AuditRunStore.AuditRunState run,
        ResolvedPlan plan,
        IContent node)
    {
        if (plan.MultiCulture && node.ContentType.VariesByCulture()
            && (plan.Culture is null || node.GetCultureName(plan.Culture) is null)) return;
        if (plan.DocumentTypes.Any(type => type.Id == node.ContentType.Id)
            && (!plan.MultiCulture || node.ContentType.VariesByCulture() == (plan.Culture is not null)))
        {
            var row = BuildDocumentRow(user, run, plan, node);
            if (row is not null && Matches(row, plan))
            {
                TryRetain(run, row);
            }
        }

        // node.ContentType is ISimpleContentType; full metadata is resolved
        // through the plan's selected/owner content types.

        if (!plan.ElementsSelected)
        {
            return;
        }

        var ownerType = plan.OwnerTypes.FirstOrDefault(type => type.Id == node.ContentType.Id);
        if (ownerType is null)
        {
            return;
        }

        var backofficePath = _hostingEnvironment.GetBackOfficePath().TrimEnd('/');
        var ownerName = DocumentName(node, plan.Culture);
        foreach (var property in ownerType.CompositionPropertyTypes)
        {
            if (!BlockEditorAliases.Contains(property.PropertyEditorAlias)
                || !PropertyAuditService.CanReadProperty(user, property))
            {
                continue;
            }
            if (plan.MultiCulture && property.VariesByCulture() && plan.Culture is null) continue;

            var propertyCulture = property.VariesByCulture() ? plan.Culture : null;
            var raw = node.GetValue(property.Alias, propertyCulture, segment: null, published: false);
            TraverseBlocks(
                user, run, plan, node, ownerName, backofficePath,
                AuditFilterCompiler.Text(raw), property.PropertyEditorAlias,
                property.Alias,
                new[]
                {
                    new AuditRunStore.AuditPropertyAccess(
                        ownerType.Key, property.Alias, property.PropertyEditorAlias),
                },
                1, ownerType.VariesByCulture() || property.VariesByCulture());
        }
    }

    /// <summary>Recursively walks block layout instances for an owner document.</summary>
    private void TraverseBlocks(
        IUser user,
        AuditRunStore.AuditRunState run,
        ResolvedPlan plan,
        IContent owner,
        string ownerName,
        string backofficePath,
        string? json,
        string editorAlias,
        string locationPrefix,
        IReadOnlyList<AuditRunStore.AuditPropertyAccess> trail,
        int depth,
        bool culturePath = false)
    {
        if (depth >= MaxNestDepth)
        {
            run.HasPartialWarnings = true;
            run.AddWarning("Block nesting exceeded the traversal limit.");
            return;
        }

        AuditBlockReadResult read;
        try
        {
            read = AuditBlockReader.Read(json, editorAlias);
        }
        catch (Exception)
        {
            run.HasPartialWarnings = true;
            run.AddWarning($"A block value at '{locationPrefix}' could not be read.");
            return;
        }

        foreach (var warning in read.Warnings)
        {
            run.HasPartialWarnings = true;
            run.AddWarning(warning);
        }

        foreach (var instance in read.Instances)
        {
            if (run.RetentionExceeded) return;
            if (plan.MultiCulture && instance.ExposedCultures is { } exposed
                && !exposed.Contains(null)
                && !exposed.Contains(plan.Culture, StringComparer.OrdinalIgnoreCase)) continue;
            // Resolve every instance to a known element type; unknown types
            // mark the run partial and are skipped entirely.
            if (!plan.AllElementTypes.TryGetValue(instance.ContentTypeKey, out var elementType))
            {
                run.HasPartialWarnings = true;
                run.AddWarning($"An unknown element type was found at '{locationPrefix}'.");
                continue;
            }

            // A row is produced only for selected element types, but nested
            // blocks are traversed for every known type so selected children
            // inside unselected wrappers are still discovered.
            var instanceCulturePath = culturePath || elementType.VariesByCulture()
                || instance.ExposedCultures?.Any(culture => culture is not null) == true;
            if (plan.ElementTypes.Any(type => type.Key == elementType.Key))
            {
                var row = BuildElementRow(
                    user, run, plan, owner, ownerName, backofficePath,
                    elementType, instance, locationPrefix, trail, instanceCulturePath);
                if (row is not null && Matches(row, plan))
                {
                    TryRetain(run, row);
                }
            }

            // Nested Block List/Grid properties on the element instance.
            foreach (var property in elementType.CompositionPropertyTypes)
            {
                if (!BlockEditorAliases.Contains(property.PropertyEditorAlias)
                    || !PropertyAuditService.CanReadProperty(user, property))
                {
                    continue;
                }
                if (plan.MultiCulture && property.VariesByCulture() && plan.Culture is null) continue;

                object? nestedRaw;
                try
                {
                    var propertyCulture = property.VariesByCulture() ? plan.Culture : null;
                    nestedRaw = AuditBlockReader.PropertyValue(instance.Data, property.Alias, propertyCulture);
                }
                catch (Exception)
                {
                    run.HasPartialWarnings = true;
                    run.AddWarning($"A nested block value at '{locationPrefix}' could not be read.");
                    continue;
                }

                TraverseBlocks(
                    user, run, plan, owner, ownerName, backofficePath,
                    AuditFilterCompiler.Text(nestedRaw), property.PropertyEditorAlias,
                    $"{locationPrefix}{instance.Path}/{property.Alias}",
                    new List<AuditRunStore.AuditPropertyAccess>(trail)
                    {
                        new(elementType.Key, property.Alias, property.PropertyEditorAlias),
                    },
                    depth + 1, instanceCulturePath || property.VariesByCulture());
            }
        }
    }

    private AuditRunStore.RetainedRow? BuildDocumentRow(
        IUser user,
        AuditRunStore.AuditRunState run,
        ResolvedPlan plan,
        IContent node)
    {
        var documentType = plan.OwnerTypes.FirstOrDefault(type => type.Id == node.ContentType.Id);
        if (documentType is null)
        {
            return null;
        }

        var backofficePath = _hostingEnvironment.GetBackOfficePath().TrimEnd('/');
        var cells = new List<AuditRunStore.RetainedCell>();
        foreach (var column in plan.Columns)
        {
            var property = documentType.CompositionPropertyTypes
                .FirstOrDefault(candidate => string.Equals(candidate.Alias, column.Alias, StringComparison.Ordinal));
            var readable = property is not null && PropertyAuditService.CanReadProperty(user, property);
            object? raw = null;
            if (readable)
            {
                var propertyCulture = property!.VariesByCulture() ? plan.Culture : null;
                raw = node.GetValue(column.Alias, propertyCulture, segment: null, published: false);
            }

            cells.Add(ToCell(column.Alias, property, readable, raw));
        }

        return new AuditRunStore.RetainedRow
        {
            RowKey = plan.MultiCulture ? $"{node.Key:D}:{plan.Culture ?? "invariant"}" : node.Key.ToString("D"),
            Kind = "document",
            TypeAlias = documentType.Alias,
            TypeName = documentType.Name ?? documentType.Alias,
            TypeKey = documentType.Key,
            OwnerDocumentKey = node.Key,
            OwnerDocumentName = DocumentName(node, plan.Culture),
            Location = "Content",
            Culture = node.ContentType.VariesByCulture() ? plan.Culture : null,
            EditUrl = $"{backofficePath}/section/content/workspace/document/edit/{node.Key:D}",
            Cells = cells,
        };
    }

    private AuditRunStore.RetainedRow? BuildElementRow(
        IUser user,
        AuditRunStore.AuditRunState run,
        ResolvedPlan plan,
        IContent owner,
        string ownerName,
        string backofficePath,
        IContentType elementType,
        AuditBlockInstance instance,
        string locationPrefix,
        IReadOnlyList<AuditRunStore.AuditPropertyAccess> trail,
        bool culturePath)
    {
        var needsCulture = culturePath || elementType.VariesByCulture()
            || elementType.CompositionPropertyTypes.Any(property => property.VariesByCulture());
        var rowCulture = needsCulture ? plan.Culture : null;
        if (plan.MultiCulture
            && ((needsCulture && plan.Culture is null)
                || !string.Equals(rowCulture, plan.Culture, StringComparison.OrdinalIgnoreCase))) return null;
        var location = $"{locationPrefix}{instance.Path}";
        var cells = new List<AuditRunStore.RetainedCell>();
        foreach (var column in plan.Columns)
        {
            var property = elementType.CompositionPropertyTypes
                .FirstOrDefault(candidate => string.Equals(candidate.Alias, column.Alias, StringComparison.Ordinal));
            var readable = property is not null && PropertyAuditService.CanReadProperty(user, property);
            object? raw = null;
            if (readable)
            {
                try
                {
                    var propertyCulture = property!.VariesByCulture() ? plan.Culture : null;
                    raw = AuditBlockReader.PropertyValue(instance.Data, column.Alias, propertyCulture);
                }
                catch (Exception)
                {
                    run.HasPartialWarnings = true;
                    run.AddWarning($"A block value at '{location}' could not be read.");
                }
            }

            cells.Add(ToCell(column.Alias, property, readable, raw));
        }

        return new AuditRunStore.RetainedRow
        {
            RowKey = $"{owner.Key:D}:{location}:{instance.Key:D}" + (plan.MultiCulture ? $":{rowCulture ?? "invariant"}" : ""),
            Kind = "element",
            TypeAlias = elementType.Alias,
            TypeName = elementType.Name ?? elementType.Alias,
            TypeKey = elementType.Key,
            OwnerDocumentKey = owner.Key,
            OwnerDocumentName = ownerName,
            Location = location,
            Culture = rowCulture,
            EditUrl = $"{backofficePath}/section/content/workspace/document/edit/{owner.Key:D}",
            Cells = cells,
            TraversalAccess = trail,
        };
    }

    private static AuditRunStore.RetainedCell ToCell(
        string alias, IPropertyType? property, bool readable, object? raw)
    {
        if (property is null)
        {
            return new AuditRunStore.RetainedCell
            {
                Alias = alias, Status = "notApplicable",
            };
        }

        if (!readable)
        {
            return new AuditRunStore.RetainedCell
            {
                Alias = alias, Status = "restricted",
            };
        }

        var text = AuditFilterCompiler.Text(raw);
        var empty = string.IsNullOrWhiteSpace(text);
        var display = text.Length > MaxDisplayValueLength
            ? text[..MaxDisplayValueLength]
            : (empty ? null : text);
        return new AuditRunStore.RetainedCell
        {
            Alias = alias,
            Status = empty ? "empty" : "filled",
            RawValue = raw,
            DisplayValue = display,
            Truncated = text.Length > MaxDisplayValueLength,
        };
    }

    private static bool Matches(AuditRunStore.RetainedRow row, ResolvedPlan plan)
    {
        foreach (var (alias, filter) in plan.RowFilters)
        {
            var value = alias switch
            {
                "kind" => row.Kind,
                "typeName" => row.TypeName,
                "content" => row.OwnerDocumentName,
                "culture" => row.Culture,
                "location" => row.Location,
                _ => null,
            };
            if (!filter.Matches(value)) return false;
        }
        bool? result = null;
        for (var i = 0; i < plan.FilterChain.Count; i++)
        {
            var link = plan.FilterChain[i];
            var cell = row.Cells.First(candidate =>
                string.Equals(candidate.Alias, link.Column.Alias, StringComparison.Ordinal));
            var applicable = cell.Status != "notApplicable";
            var readable = cell.Status != "restricted";
            var match = link.Column.Filter!.Matches(cell.RawValue, applicable, readable);
            result = result is null
                ? match
                : plan.FilterChain[i - 1].Joiner == "or" ? result.Value || match : result.Value && match;
        }

        return result ?? true;
    }

    /// <summary>
    /// Retains a matching row under the retained-row and retained-character
    /// caps. Exceeding either marks the run for a limited (never exact) result.
    /// </summary>
    private static void TryRetain(AuditRunStore.AuditRunState run, AuditRunStore.RetainedRow row)
    {
        var characters = row.Cells
            .Sum(cell => AuditFilterCompiler.Text(cell.RawValue).Length);
        if (run.Rows.Count >= MaxRetainedRows
            || run.RetainedCells + row.Cells.Count > MaxRetainedCells
            || run.RetainedCharacters + characters > MaxRetainedCharacters)
        {
            run.RetentionExceeded = true;
            return;
        }

        run.Rows.Add(row);
        run.RetainedCells += row.Cells.Count;
        run.RetainedCharacters += characters;
    }

    // ---- mapping ------------------------------------------------------------------

    private static string DocumentName(IContent node, string? culture) =>
        node.ContentType.VariesByCulture()
            ? (culture is null ? "(unnamed)" : node.GetCultureName(culture) ?? "(unnamed)")
            : node.Name ?? "(unnamed)";

    private static AuditRunProgress ToProgress(
        AuditRunStore.AuditRunState run,
        IReadOnlyCollection<AuditRunStore.RetainedRow>? accessible = null)
    {
        var matched = accessible?.Count ?? run.Rows.Count;
        return new AuditRunProgress
        {
            RunId = run.RunId,
            Status = run.Status,
            ScannedCount = run.ScannedCount,
            MatchedCount = matched,
            TotalCount = run.Status == "completed" ? matched : null,
            StartedUtc = run.StartedUtc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(run.StartedUtc, DateTimeKind.Utc)
                : run.StartedUtc,
            CompletedUtc = run.CompletedUtc,
            Warnings = run.Warnings.ToArray(),
        };
    }

    private static AuditRunRow ToRowDto(AuditRunStore.RetainedRow row) =>
        new()
        {
            RowKey = row.RowKey,
            Kind = row.Kind,
            TypeAlias = row.TypeAlias,
            TypeName = row.TypeName,
            OwnerDocumentKey = row.OwnerDocumentKey,
            OwnerDocumentName = row.OwnerDocumentName,
            Location = row.Location,
            Culture = row.Culture,
            EditUrl = row.EditUrl,
            Cells = row.Cells.Select(cell => new AuditRunCell
            {
                Alias = cell.Alias,
                Status = cell.Status,
                Value = cell.Status is "empty" or "notApplicable" or "restricted"
                    ? null
                    : cell.DisplayValue,
                Truncated = cell.Truncated,
            }).ToArray(),
        };

    // ---- sorting -----------------------------------------------------------------

    private sealed class ResolvedSort
    {
        public string Kind { get; init; } = "rowKey";
        public string? Alias { get; init; }
        public string? Error { get; init; }
        public AuditColumnDefinition? Column { get; init; }
    }

    private static ResolvedSort ResolveSort(string? sortBy, AuditDefinition definition)
    {
        if (sortBy is null or "")
        {
            return new ResolvedSort();
        }

        if (sortBy is "kind" or "typeName" or "content" or "location" or "culture")
        {
            return new ResolvedSort { Kind = sortBy };
        }

        const string prefix = "property:";
        if (sortBy.StartsWith(prefix, StringComparison.Ordinal))
        {
            var alias = sortBy[prefix.Length..];
            var column = definition.Columns.FirstOrDefault(candidate =>
                string.Equals(candidate.Alias?.Trim(), alias, StringComparison.Ordinal));
            return column is null
                ? new ResolvedSort { Error = $"Unknown sort alias '{alias}'." }
                : new ResolvedSort { Kind = "property", Alias = alias, Column = column };
        }

        return new ResolvedSort { Error = $"Unknown sort field '{sortBy}'." };
    }

    private sealed class RetainedRowComparer : IComparer<AuditRunStore.RetainedRow>
    {
        private readonly ResolvedSort _sort;
        private readonly int _direction;

        public RetainedRowComparer(ResolvedSort sort, bool descending)
        {
            _sort = sort;
            _direction = descending ? -1 : 1;
        }

        public int Compare(AuditRunStore.RetainedRow? x, AuditRunStore.RetainedRow? y)
        {
            var aAvailable = TrySortValue(x!, out var a);
            var bAvailable = TrySortValue(y!, out var b);
            int comparison;
            if (!aAvailable && !bAvailable)
            {
                comparison = 0;
            }
            else if (!aAvailable)
            {
                comparison = 1; // unavailable sorts last in both directions
            }
            else if (!bAvailable)
            {
                comparison = -1;
            }
            else
            {
                comparison = _direction * CompareValues(a, b);
            }

            return comparison != 0
                ? comparison
                : string.CompareOrdinal(x!.RowKey, y!.RowKey);
        }

        private bool TrySortValue(AuditRunStore.RetainedRow row, out object? value)
        {
            value = null;
            switch (_sort.Kind)
            {
                case "kind":
                    value = row.Kind;
                    return true;
                case "typeName":
                    value = row.TypeName;
                    return true;
                case "content":
                    value = row.OwnerDocumentName;
                    return true;
                case "location":
                    value = row.Location;
                    return true;
                case "culture":
                    value = row.Culture ?? "";
                    return true;
                case "property":
                    var cell = row.Cells.FirstOrDefault(candidate =>
                        string.Equals(candidate.Alias, _sort.Alias, StringComparison.Ordinal));
                    if (cell is null || cell.Status is "notApplicable" or "restricted" or "empty")
                    {
                        return false;
                    }

                    var raw = cell.RawValue;
                    var text = AuditFilterCompiler.Text(raw);
                    switch (_sort.Column!.ValueType)
                    {
                        case "number":
                            if (!AuditFilterCompiler.Number(text, out var number)) return false;
                            value = number;
                            return true;
                        case "date":
                            if (!AuditFilterCompiler.Date(raw, out var date)) return false;
                            value = date;
                            return true;
                        case "boolean":
                            if (!AuditFilterCompiler.Boolean(raw, out var boolean)) return false;
                            value = boolean;
                            return true;
                        default:
                            value = text;
                            return true;
                    }
                default:
                    value = row.RowKey;
                    return true;
            }
        }

        private int CompareValues(object? a, object? b) => (a, b) switch
        {
            (decimal x, decimal y) => x.CompareTo(y),
            (DateTimeOffset x, DateTimeOffset y) => x.CompareTo(y),
            (bool x, bool y) => x.CompareTo(y),
            (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
            _ => 0,
        };
    }

    // ---- results -----------------------------------------------------------------

    public sealed class RunResult<T>
    {
        public enum StatusKind { Ok, NotFound, Invalid, Forbidden, Conflict, TooMany, Unavailable }

        public StatusKind Status { get; private init; }
        public T? Value { get; private init; }
        public Dictionary<string, string[]>? Errors { get; private init; }
        public string? Message { get; private init; }

        public static readonly RunResult<T> NotFound = new() { Status = StatusKind.NotFound };
        public static readonly RunResult<T> TooMany = new() { Status = StatusKind.TooMany };
        public static readonly RunResult<T> Unavailable = new() { Status = StatusKind.Unavailable };
        public static RunResult<T> Ok(T value) => new() { Status = StatusKind.Ok, Value = value };
        public static RunResult<T> Invalid(Dictionary<string, string[]> errors) =>
            new() { Status = StatusKind.Invalid, Errors = errors };
        public static RunResult<T> Forbidden(string message) =>
            new() { Status = StatusKind.Forbidden, Message = message };
        public static RunResult<T> Conflict(string message) =>
            new() { Status = StatusKind.Conflict, Message = message };
    }
}

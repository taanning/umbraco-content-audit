using PropertyAuditTool.Models;
using AuditItem = PropertyAuditTool.Models.AuditItem;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Hosting;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Models.Membership.Permissions;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace PropertyAuditTool.Services;

/// <summary>
/// Read-only audit of persisted document property values. Inspects at most
/// <see cref="MaxCandidatesPerRequest"/> raw candidates per request in database
/// pages of <see cref="PageSize"/>, ordered by numeric node ID. Pagination is not
/// a database snapshot: concurrent edits/deletions can shift results.
/// </summary>
public sealed class PropertyAuditService
{
    public const int PageSize = 250;
    public const int MaxCandidatesPerRequest = 2000;
    public const int MaxAliasLength = 255;
    public const int MaxSearchValueLength = 2000;
    public const int MinTake = 1;
    public const int MaxTake = 200;

    /// <summary>Document-property UI read verb ("Umb.Document.PropertyValue.Read").</summary>
    public const string PropertyValueReadVerb = "Umb.Document.PropertyValue.Read";

    private readonly IContentTypeService _contentTypeService;
    private readonly IContentService _contentService;
    private readonly ILanguageService _languageService;
    private readonly IContentPermissionAuthorizer _contentPermissionAuthorizer;
    private readonly IHostingEnvironment _hostingEnvironment;

    public PropertyAuditService(
        IContentTypeService contentTypeService,
        IContentService contentService,
        ILanguageService languageService,
        IContentPermissionAuthorizer contentPermissionAuthorizer,
        IHostingEnvironment hostingEnvironment)
    {
        _contentTypeService = contentTypeService;
        _contentService = contentService;
        _languageService = languageService;
        _contentPermissionAuthorizer = contentPermissionAuthorizer;
        _hostingEnvironment = hostingEnvironment;
    }

    /// <summary>
    /// Whether the user may read the property's value in the backoffice UI.
    /// Granular document-property-value permissions keyed by the property type key
    /// override the union of user group permissions; without a granular entry for
    /// that key, the plain group permissions decide.
    /// </summary>
    public static bool CanReadProperty(IUser user, IPropertyType propertyType)
    {
        var matchingGranular = user.Groups
            .SelectMany(group => group.GranularPermissions)
            .OfType<DocumentPropertyValueGranularPermission>()
            .Select(permission => permission.Permission?.Split('|') ?? Array.Empty<string>())
            .Where(parts => parts.Length == 2
                && Guid.TryParse(parts[0], out var key)
                && key == propertyType.Key)
            .ToArray();

        if (matchingGranular.Length > 0)
        {
            // Union verbs across user groups; an empty verb is an explicit denial.
            return matchingGranular.Any(parts =>
                string.Equals(parts[1], PropertyValueReadVerb, StringComparison.Ordinal));
        }

        return user.Groups
            .SelectMany(group => group.Permissions)
            .Contains(PropertyValueReadVerb, StringComparer.Ordinal);
    }

    public async Task<AuditOperationResult<CatalogResponse>> GetCatalogAsync(IUser user)
    {
        var documentTypes = _contentTypeService.GetAll()
            .Where(contentType => !contentType.IsElement)
            .OrderBy(contentType => contentType.Alias, StringComparer.Ordinal)
            .Select(contentType => new DocumentTypeModel
            {
                Alias = contentType.Alias,
                Name = contentType.Name ?? contentType.Alias,
                VariesByCulture = contentType.VariesByCulture(),
                Properties = contentType.CompositionPropertyTypes
                    .Where(propertyType => CanReadProperty(user, propertyType))
                    .OrderBy(propertyType => propertyType.Alias, StringComparer.Ordinal)
                    .Select(propertyType => new PropertyTypeModel
                    {
                        Alias = propertyType.Alias,
                        Name = propertyType.Name ?? propertyType.Alias,
                        EditorAlias = propertyType.PropertyEditorAlias,
                        VariesByCulture = propertyType.VariesByCulture(),
                        VariesBySegment = propertyType.VariesBySegment(),
                    })
                    .ToArray(),
            })
            .ToArray();

        return AuditOperationResult<CatalogResponse>.Success(new CatalogResponse
        {
            DocumentTypes = documentTypes,
            Languages = await GetPermittedLanguagesAsync(user),
        });
    }

    /// <summary>
    /// V2 catalog: document AND element types with viewer-readable composed
    /// properties and storage/editor hints, plus permitted cultures.
    /// </summary>
    public async Task<AuditOperationResult<AuditCatalogResponse>> GetAuditCatalogAsync(IUser user)
    {
        var types = _contentTypeService.GetAll()
            .OrderBy(contentType => contentType.Alias, StringComparer.Ordinal)
            .Select(contentType => new PropertyAuditTypeModel
            {
                Kind = contentType.IsElement ? "element" : "document",
                Alias = contentType.Alias,
                Name = contentType.Name ?? contentType.Alias,
                Unique = contentType.Key,
                VariesByCulture = contentType.VariesByCulture(),
                Properties = contentType.CompositionPropertyTypes
                    .Where(propertyType => CanReadProperty(user, propertyType))
                    .OrderBy(propertyType => propertyType.Alias, StringComparer.Ordinal)
                    .Select(propertyType => new AuditCatalogPropertyModel
                    {
                        Alias = propertyType.Alias,
                        Name = propertyType.Name ?? propertyType.Alias,
                        EditorAlias = propertyType.PropertyEditorAlias,
                        ValueTypeHint = ValueTypeHint(propertyType),
                        VariesByCulture = propertyType.VariesByCulture(),
                        VariesBySegment = propertyType.VariesBySegment(),
                    })
                    .ToArray(),
            })
            .ToArray();

        return AuditOperationResult<AuditCatalogResponse>.Success(new AuditCatalogResponse
        {
            Types = types,
            Languages = await GetPermittedLanguagesAsync(user),
        });
    }

    /// <summary>Storage/editor-derived column type hint for the audit builder.</summary>
    public static string ValueTypeHint(IPropertyType propertyType) =>
        propertyType.PropertyEditorAlias == "Umbraco.TrueFalse"
            ? "boolean"
            : propertyType.ValueStorageType switch
            {
                ValueStorageType.Integer or ValueStorageType.Decimal => "number",
                ValueStorageType.Date => "date",
                _ => "text",
            };

    private async Task<IReadOnlyList<LanguageModel>> GetPermittedLanguagesAsync(IUser user)
    {
        var languages = await _languageService.GetAllAsync();
        var permittedLanguages = new List<LanguageModel>();
        foreach (var language in languages)
        {
            if (await _contentPermissionAuthorizer.IsDeniedForCultures(user, new HashSet<string> { language.IsoCode }))
            {
                continue;
            }

            permittedLanguages.Add(new LanguageModel
            {
                IsoCode = language.IsoCode,
                Name = language.CultureName ?? language.IsoCode,
                IsDefault = language.IsDefault,
            });
        }

        return permittedLanguages
            .OrderByDescending(language => language.IsDefault)
            .ThenBy(language => language.IsoCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<AuditOperationResult<AuditQueryResponse>> QueryAsync(
        IUser user,
        AuditQueryRequest request,
        CancellationToken cancellationToken)
    {
        // ---- normalize ----------------------------------------------------
        var documentTypeAlias = NormalizeOptional(request.DocumentTypeAlias);
        var culture = NormalizeOptional(request.Culture);
        var propertyAlias = request.PropertyAlias?.Trim();
        var @operator = request.Operator;
        var searchValue = request.SearchValue;

        // ---- input validation ----------------------------------------------
        var errors = new Dictionary<string, string[]>();
        void AddError(string field, string message) => errors[field] = new[] { message };

        if (string.IsNullOrEmpty(propertyAlias))
        {
            AddError("propertyAlias", "A property alias is required.");
        }
        else if (propertyAlias.Length > MaxAliasLength)
        {
            AddError("propertyAlias", $"Property alias must be {MaxAliasLength} characters or fewer.");
        }

        if (documentTypeAlias?.Length > MaxAliasLength)
        {
            AddError("documentTypeAlias", $"Document type alias must be {MaxAliasLength} characters or fewer.");
        }

        if (string.IsNullOrEmpty(@operator) || !AuditValueEvaluator.SupportedOperators.Contains(@operator))
        {
            AddError("operator", "Operator must be one of: isEmpty, hasText, contains, equals.");
        }

        if (searchValue?.Length > MaxSearchValueLength)
        {
            AddError("searchValue", $"Search value must be {MaxSearchValueLength} characters or fewer.");
        }
        else if (@operator is not null
            && AuditValueEvaluator.RequiresSearchValue(@operator)
            && string.IsNullOrWhiteSpace(searchValue))
        {
            AddError("searchValue", $"A non-empty search value is required for the '{@operator}' operator.");
        }

        if (request.Skip < 0)
        {
            AddError("skip", "Skip must be 0 or greater.");
        }

        if (request.Take is < MinTake or > MaxTake)
        {
            AddError("take", $"Take must be between {MinTake} and {MaxTake}.");
        }

        if (errors.Count > 0)
        {
            return AuditOperationResult<AuditQueryResponse>.ValidationError(errors);
        }

        // ---- resolve candidate document types ------------------------------
        var selectedTypes = _contentTypeService.GetAll()
            .Where(contentType => !contentType.IsElement)
            .ToArray();

        if (documentTypeAlias is not null)
        {
            selectedTypes = selectedTypes
                .Where(contentType => string.Equals(contentType.Alias, documentTypeAlias, StringComparison.Ordinal))
                .ToArray();

            if (selectedTypes.Length == 0)
            {
                AddError("documentTypeAlias", $"Unknown document type alias '{documentTypeAlias}'.");
                return AuditOperationResult<AuditQueryResponse>.ValidationError(errors);
            }
        }

        // Types that actually define the property (including via compositions).
        var definingCandidates = selectedTypes
            .Select(contentType => new
            {
                ContentType = contentType,
                PropertyType = contentType.CompositionPropertyTypes
                    .FirstOrDefault(p => string.Equals(p.Alias, propertyAlias, StringComparison.Ordinal)),
            })
            .Where(candidate => candidate.PropertyType is not null)
            .ToArray();

        if (definingCandidates.Length == 0)
        {
            AddError("propertyAlias", $"No selected document type defines the property alias '{propertyAlias}'.");
            return AuditOperationResult<AuditQueryResponse>.ValidationError(errors);
        }

        // The property exists, but the user may not be allowed to read its value.
        var candidates = definingCandidates
            .Where(candidate => CanReadProperty(user, candidate.PropertyType!))
            .ToArray();

        if (candidates.Length == 0)
        {
            return AuditOperationResult<AuditQueryResponse>.Forbidden(
                $"You do not have permission to read the property '{propertyAlias}'.");
        }

        // ---- variation validation -------------------------------------------
        if (candidates.Any(candidate => candidate.PropertyType!.VariesBySegment()))
        {
            AddError("propertyAlias", $"The property '{propertyAlias}' varies by segment, which is not supported.");
            return AuditOperationResult<AuditQueryResponse>.ValidationError(errors);
        }

        var cultureRequired = candidates.Any(candidate =>
            candidate.ContentType.VariesByCulture() || candidate.PropertyType!.VariesByCulture());

        var languages = await _languageService.GetAllAsync();
        string? normalizedCulture = null;
        if (culture is not null)
        {
            normalizedCulture = languages
                .FirstOrDefault(language => string.Equals(language.IsoCode, culture, StringComparison.OrdinalIgnoreCase))
                ?.IsoCode;

            if (normalizedCulture is null)
            {
                AddError("culture", $"Unknown culture '{culture}'.");
                return AuditOperationResult<AuditQueryResponse>.ValidationError(errors);
            }

            if (await _contentPermissionAuthorizer.IsDeniedForCultures(user, new HashSet<string> { normalizedCulture }))
            {
                return AuditOperationResult<AuditQueryResponse>.Forbidden(
                    $"You do not have permission to read culture '{normalizedCulture}'.");
            }
        }

        if (cultureRequired && normalizedCulture is null)
        {
            AddError("culture", "A culture is required because the document type or property varies by culture.");
            return AuditOperationResult<AuditQueryResponse>.ValidationError(errors);
        }

        // ---- scan -----------------------------------------------------------
        // Candidate metadata keyed by content type id.
        var candidatesByContentTypeId = candidates.ToDictionary(candidate => candidate.ContentType.Id);
        var contentTypeIds = candidatesByContentTypeId.Keys.ToArray();

        var backofficePath = _hostingEnvironment.GetBackOfficePath().TrimEnd('/');

        var cursor = request.Skip;
        var visited = 0L;
        var scannedCount = 0L;
        var items = new List<AuditItem>();
        var totalRecords = 0L;
        var exhausted = false;

        while (visited < MaxCandidatesPerRequest && items.Count < request.Take)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageIndex = cursor / PageSize;
            var withinPage = (int)(cursor % PageSize);

            var page = _contentService.GetPagedOfTypes(
                contentTypeIds,
                pageIndex,
                PageSize,
                out totalRecords,
                filter: null,
                ordering: Ordering.By("Id")).ToArray();

            if (cursor >= totalRecords || page.Length <= withinPage)
            {
                exhausted = true;
                break;
            }

            var candidateKeys = page.Where(node => !node.Trashed).Select(node => node.Key).ToArray();
            var authorizedKeys = await AuthorizeBatchAsync(user, candidateKeys, cancellationToken);

            for (var index = withinPage; index < page.Length && visited < MaxCandidatesPerRequest && items.Count < request.Take; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var node = page[index];
                cursor++;
                visited++;

                if (node.Trashed || !authorizedKeys.Contains(node.Key))
                {
                    continue;
                }

                if (!node.Properties.TryGetValue(propertyAlias!, out var property))
                {
                    continue;
                }

                // Defense in depth: the concrete property on the node must also be readable.
                if (!CanReadProperty(user, property.PropertyType))
                {
                    continue;
                }

                scannedCount++;

                var cultureForProperty = property.PropertyType.VariesByCulture()
                    ? normalizedCulture
                    : null;

                var rawValue = node.GetValue(propertyAlias!, cultureForProperty, segment: null, published: false);
                var (matches, isEmpty) = AuditValueEvaluator.Evaluate(@operator!, rawValue, searchValue);

                if (!matches)
                {
                    continue;
                }

                items.Add(new AuditItem
                {
                    NodeId = node.Id,
                    Unique = node.Key,
                    Name = node.ContentType.VariesByCulture()
                        ? node.GetCultureName(normalizedCulture!) ?? "(unnamed)"
                        : node.Name ?? "(unnamed)",
                    DocumentTypeAlias = node.ContentType.Alias,
                    UpdatedDate = node.UpdateDate,
                    PropertyStatus = isEmpty ? "Empty" : "Filled",
                    EditUrl = $"{backofficePath}/section/content/workspace/document/edit/{node.Key:D}",
                });
            }
        }

        return AuditOperationResult<AuditQueryResponse>.Success(new AuditQueryResponse
        {
            Items = items,
            NextSkip = !exhausted && cursor < totalRecords ? cursor : null,
            ScannedCount = scannedCount,
        });
    }

    /// <summary>
    /// Umbraco 17.0-compatible batch authorization: the batch call returns only
    /// "any denied", so denied batches are halved recursively until fast-path
    /// accepted batches and excluded singleton denials are found.
    /// </summary>
    private async Task<ISet<Guid>> AuthorizeBatchAsync(
        IUser user,
        IReadOnlyCollection<Guid> keys,
        CancellationToken cancellationToken)
    {
        var authorized = new HashSet<Guid>();
        if (keys.Count == 0)
        {
            return authorized;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!await _contentPermissionAuthorizer.IsDeniedAsync(
                user, keys, new HashSet<string> { ActionBrowse.ActionLetter }))
        {
            authorized.UnionWith(keys);
            return authorized;
        }

        if (keys.Count == 1)
        {
            return authorized;
        }

        var half = keys.Count / 2;
        var first = keys.Take(half).ToArray();
        var second = keys.Skip(half).ToArray();
        authorized.UnionWith(await AuthorizeBatchAsync(user, first, cancellationToken));
        authorized.UnionWith(await AuthorizeBatchAsync(user, second, cancellationToken));
        return authorized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PropertyAuditTool.Models;
using PropertyAuditTool.Services;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace PropertyAuditTool.Controllers;

/// <summary>
/// Management API for the Content Completeness Audit &amp; Query package.
/// Read-only; requires backoffice authentication and Content section access.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("property-audit")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
[ApiExplorerSettings(GroupName = "PropertyAuditTool")]
public class PropertyAuditApiController : ManagementApiControllerBase
{
    private readonly IBackOfficeSecurityAccessor _backOfficeSecurityAccessor;
    private readonly PropertyAuditService _propertyAuditService;

    public PropertyAuditApiController(
        IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
        PropertyAuditService propertyAuditService)
    {
        _backOfficeSecurityAccessor = backOfficeSecurityAccessor;
        _propertyAuditService = propertyAuditService;
    }

    /// <summary>
    /// Document types (with composed, readable properties) and permitted languages.
    /// </summary>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(CatalogResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalog(CancellationToken cancellationToken)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await _propertyAuditService.GetCatalogAsync(user);
        return ToActionResult(result);
    }

    /// <summary>
    /// Scans up to 2000 saved raw candidates and returns matching documents.
    /// </summary>
    [HttpPost("query")]
    [ProducesResponseType(typeof(AuditQueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Query(
        [FromBody] AuditQueryRequest request,
        CancellationToken cancellationToken)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await _propertyAuditService.QueryAsync(user, request, cancellationToken);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(AuditOperationResult<T> result)
    {
        if (result.Succeeded)
        {
            return Ok(result.Value);
        }

        if (result.ErrorType == AuditErrorType.Forbidden)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden",
                Detail = result.ForbiddenMessage,
                Status = StatusCodes.Status403Forbidden,
            });
        }

        foreach (var (field, messages) in result.Errors ?? new Dictionary<string, string[]>())
        {
            foreach (var message in messages)
            {
                ModelState.AddModelError(field, message);
            }
        }

        return ValidationProblem(ModelState);
    }
}

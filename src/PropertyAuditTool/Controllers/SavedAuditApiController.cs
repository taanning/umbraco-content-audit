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
/// Management API for saved audits (schema v2): own private definitions plus
/// all shared definitions, owner-only mutations with optimistic versioning,
/// and per-viewer dashboard preferences.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("property-audit/audit")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
[ApiExplorerSettings(GroupName = "PropertyAuditTool")]
public class SavedAuditApiController : ManagementApiControllerBase
{
    private readonly IBackOfficeSecurityAccessor _backOfficeSecurityAccessor;
    private readonly SavedAuditService _savedAuditService;
    private readonly PropertyAuditService _propertyAuditService;

    public SavedAuditApiController(
        IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
        SavedAuditService savedAuditService,
        PropertyAuditService propertyAuditService)
    {
        _backOfficeSecurityAccessor = backOfficeSecurityAccessor;
        _savedAuditService = savedAuditService;
        _propertyAuditService = propertyAuditService;
    }

    /// <summary>Document and element type metadata plus permitted cultures.</summary>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(AuditCatalogResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditCatalog(CancellationToken cancellationToken)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await _propertyAuditService.GetAuditCatalogAsync(user);
        return Ok(result.Value);
    }

    /// <summary>Own audits plus every shared audit, with viewer preferences.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AuditResponse>), StatusCodes.Status200OK)]
    public IActionResult GetAudits()
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(_savedAuditService.List(user).Items);
    }

    /// <summary>Single owned or shared audit; 404 when invisible.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AuditResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetAudit(Guid id)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedAuditService.Get(user, id);
        return result.Response is null ? NotFound() : Ok(result.Response);
    }

    /// <summary>Creates an audit owned by the authenticated user.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(AuditResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult CreateAudit([FromBody] AuditWriteRequest request)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedAuditService.Create(user, request);
        if (result.Response is null)
        {
            return ValidationProblemResult(result.Errors);
        }

        return CreatedAtAction(
            nameof(GetAudit),
            new { id = result.Response.Id },
            result.Response);
    }

    /// <summary>Owner-only update with optimistic versioning.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(AuditResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult UpdateAudit(Guid id, [FromBody] AuditWriteRequest request)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedAuditService.Update(user, id, request);
        return result.Status switch
        {
            SavedAuditService.SavedAuditUpdateResult.StatusKind.NotFound => NotFound(),
            SavedAuditService.SavedAuditUpdateResult.StatusKind.Conflict => Conflict(),
            SavedAuditService.SavedAuditUpdateResult.StatusKind.Invalid =>
                ValidationProblemResult(result.Errors),
            _ => Ok(result.Response),
        };
    }

    /// <summary>Owner-only delete with optimistic versioning; 204 on success.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult DeleteAudit(Guid id, [FromQuery] int? version)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedAuditService.Delete(user, id, version);
        return result.Status switch
        {
            SavedAuditService.SavedAuditDeleteResult.StatusKind.Deleted => NoContent(),
            SavedAuditService.SavedAuditDeleteResult.StatusKind.NotFound => NotFound(),
            SavedAuditService.SavedAuditDeleteResult.StatusKind.Conflict => Conflict(),
            _ => ValidationProblemResult(result.Errors),
        };
    }

    /// <summary>
    /// Current viewer's personal pin/icon/colour preference. Requires read
    /// access to the audit, not ownership.
    /// </summary>
    [HttpPut("{id:guid}/dashboard")]
    [ProducesResponseType(typeof(AuditResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult SetAuditDashboardPreference(
        Guid id, [FromBody] AuditDashboardPreferenceWriteRequest request)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedAuditService.SetDashboardPreference(user, id, request);
        return result.Status switch
        {
            SavedAuditService.SavedAuditPreferenceResult.StatusKind.NotFound => NotFound(),
            SavedAuditService.SavedAuditPreferenceResult.StatusKind.Invalid =>
                ValidationProblemResult(result.Errors),
            _ => Ok(result.Response),
        };
    }

    private IActionResult ValidationProblemResult(Dictionary<string, string[]>? errors)
    {
        foreach (var (field, messages) in errors ?? new Dictionary<string, string[]>())
        {
            foreach (var message in messages)
            {
                ModelState.AddModelError(field, message);
            }
        }

        return ValidationProblem(ModelState);
    }
}

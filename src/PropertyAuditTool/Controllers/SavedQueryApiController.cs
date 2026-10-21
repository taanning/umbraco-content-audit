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
/// Management API for saved audit queries: personal records plus shared
/// records readable by every Content user; mutations are owner-only with
/// optimistic versioning.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("property-audit/saved-query")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
[ApiExplorerSettings(GroupName = "PropertyAuditTool")]
public class SavedQueryApiController : ManagementApiControllerBase
{
    private readonly IBackOfficeSecurityAccessor _backOfficeSecurityAccessor;
    private readonly SavedQueryService _savedQueryService;

    public SavedQueryApiController(
        IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
        SavedQueryService savedQueryService)
    {
        _backOfficeSecurityAccessor = backOfficeSecurityAccessor;
        _savedQueryService = savedQueryService;
    }

    /// <summary>Own records plus every shared record.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SavedQueryResponse>), StatusCodes.Status200OK)]
    public IActionResult GetSavedQueries()
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(_savedQueryService.List(user).Items);
    }

    /// <summary>Single owned or shared record; 404 when invisible.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(SavedQueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSavedQuery(Guid id)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedQueryService.Get(user, id);
        return result.Response is null ? NotFound() : Ok(result.Response);
    }

    /// <summary>Creates a record owned by the authenticated user.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(SavedQueryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult CreateSavedQuery([FromBody] SavedQueryWriteRequest request)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedQueryService.Create(user, request);
        if (result.Response is null)
        {
            return ValidationProblemResult(result.Errors);
        }

        return CreatedAtAction(
            nameof(GetSavedQuery),
            new { id = result.Response.Id },
            result.Response);
    }

    /// <summary>Owner-only update with optimistic versioning.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(SavedQueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult UpdateSavedQuery(Guid id, [FromBody] SavedQueryWriteRequest request)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedQueryService.Update(user, id, request);
        return result.Status switch
        {
            SavedQueryService.SavedQueryUpdateResult.StatusKind.NotFound => NotFound(),
            SavedQueryService.SavedQueryUpdateResult.StatusKind.Conflict => Conflict(),
            SavedQueryService.SavedQueryUpdateResult.StatusKind.Invalid =>
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
    public IActionResult DeleteSavedQuery(Guid id, [FromQuery] int? version)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = _savedQueryService.Delete(user, id, version);
        return result.Status switch
        {
            SavedQueryService.SavedQueryDeleteResult.StatusKind.Deleted => NoContent(),
            SavedQueryService.SavedQueryDeleteResult.StatusKind.NotFound => NotFound(),
            SavedQueryService.SavedQueryDeleteResult.StatusKind.Conflict => Conflict(),
            _ => ValidationProblemResult(result.Errors),
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

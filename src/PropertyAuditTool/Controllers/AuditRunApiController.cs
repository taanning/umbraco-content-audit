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
/// Viewer-owned incremental audit runs. Run state is transient and in-memory;
/// every endpoint revalidates the current viewer's access.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("property-audit/run")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
[ApiExplorerSettings(GroupName = "PropertyAuditTool")]
public class AuditRunApiController : ManagementApiControllerBase
{
    private readonly IBackOfficeSecurityAccessor _backOfficeSecurityAccessor;
    private readonly AuditExecutionService _auditExecutionService;

    public AuditRunApiController(
        IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
        AuditExecutionService auditExecutionService)
    {
        _backOfficeSecurityAccessor = backOfficeSecurityAccessor;
        _auditExecutionService = auditExecutionService;
    }

    /// <summary>Creates a draft or saved-audit run for the current viewer.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(AuditRunProgress), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateRun(
        [FromBody] AuditRunCreateRequest request, CancellationToken cancellationToken)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await _auditExecutionService.CreateAsync(user, request, cancellationToken);
        return result.Status switch
        {
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.NotFound => NotFound(),
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Invalid =>
                ValidationProblemResult(result.Errors),
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden =>
                Forbidden(result.Message),
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.TooMany =>
                StatusCode(StatusCodes.Status429TooManyRequests),
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Unavailable =>
                StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => StatusCode(StatusCodes.Status201Created, result.Value),
        };
    }

    /// <summary>Advances the run by one bounded batch.</summary>
    [HttpPost("{runId:guid}/advance")]
    [ProducesResponseType(typeof(AuditRunProgress), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AdvanceRun(Guid runId, CancellationToken cancellationToken)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await _auditExecutionService.AdvanceAsync(user, runId, cancellationToken);
        return ProgressResult(result);
    }

    /// <summary>Current progress of the viewer's run.</summary>
    [HttpGet("{runId:guid}")]
    [ProducesResponseType(typeof(AuditRunProgress), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRun(Guid runId, CancellationToken cancellationToken)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await _auditExecutionService.GetProgressAsync(user, runId, cancellationToken);
        return ProgressResult(result);
    }

    /// <summary>Globally sorted/paged retained results of a finished run.</summary>
    [HttpGet("{runId:guid}/results")]
    [ProducesResponseType(typeof(AuditRunResults), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetRunResults(
        Guid runId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = AuditExecutionService.DefaultTake,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? direction = null,
        CancellationToken cancellationToken = default)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await _auditExecutionService.GetResultsAsync(
            user, runId, skip, take, sortBy, direction, cancellationToken);
        return result.Status switch
        {
            AuditExecutionService.RunResult<AuditRunResults>.StatusKind.NotFound => NotFound(),
            AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Invalid =>
                ValidationProblemResult(result.Errors),
            AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Forbidden =>
                Forbidden(result.Message),
            AuditExecutionService.RunResult<AuditRunResults>.StatusKind.Conflict =>
                Conflict(result.Message),
            _ => Ok(result.Value),
        };
    }

    /// <summary>Cancels and removes the viewer's transient run; 204 on success.</summary>
    [HttpDelete("{runId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRun(Guid runId, CancellationToken cancellationToken)
    {
        var user = _backOfficeSecurityAccessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            return Unauthorized();
        }

        var result = await _auditExecutionService.CancelAsync(user, runId, cancellationToken);
        return result.Status == AuditExecutionService.RunResult<object>.StatusKind.NotFound
            ? NotFound()
            : NoContent();
    }

    private IActionResult ProgressResult(
        AuditExecutionService.RunResult<AuditRunProgress> result) =>
        result.Status switch
        {
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.NotFound => NotFound(),
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Invalid =>
                ValidationProblemResult(result.Errors),
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Forbidden =>
                Forbidden(result.Message),
            AuditExecutionService.RunResult<AuditRunProgress>.StatusKind.Conflict =>
                Conflict(result.Message),
            _ => Ok(result.Value),
        };

    private IActionResult Forbidden(string? message) =>
        StatusCode(StatusCodes.Status403Forbidden, new { message });

    private IActionResult Conflict(string? message) =>
        StatusCode(StatusCodes.Status409Conflict, new { message });

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

namespace CaseManagement.Api.Controllers;

using CaseManagement.Api.HostIntegration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

/// <summary>
/// How a signed-out UI may sign in to this deployment. Anonymous (see <see cref="HostIdentityMiddleware"/>) and
/// carries no secrets: only which identity sources the backend accepts.
/// </summary>
[ApiController]
[Route("api/session")]
[NoPermissionRequired]
public class SessionController(IOptions<HostIntegrationOptions> options, IHostEnvironment env) : ControllerBase
{
    /// <summary>
    /// <c>hostTokens</c>: a Host-issued token is accepted (inside the OmniConnect Host).
    /// <c>demoUsers</c>: a user may be picked without a password (the standalone site's demo user picker).
    /// </summary>
    [HttpGet("options")]
    public IActionResult GetOptions()
    {
        var mode = options.Value.ResolveMode(env.IsDevelopment());
        return Ok(new
        {
            mode = mode.ToString(),
            hostTokens = HostIntegrationOptions.AcceptsHostTokens(mode),
            demoUsers = options.Value.AcceptsDevHeader(mode)
        });
    }
}

using CaseManagement.Api.HostIntegration;
using Microsoft.AspNetCore.Mvc;

namespace CaseManagement.Api.Controllers;

/// <summary>
/// Permissions and navigation discovery: what AuthService stores in AuthDb for this app, and what the
/// Host sidebar is built from.
/// </summary>
/// <remarks>
/// Same contract as the other OmniConnect remotes' <c>GET /permissions</c> (the v4 shape):
/// <c>modules[]</c> each with <c>capabilities[]</c> and <c>nav[]</c>, plus the v1 flat
/// <c>capabilities</c> array for older readers. Register the app in Setup → Applications with the
/// permissions source URL <c>http://localhost:5110/api/casemanagement-service/permissions</c>, or press
/// Resync after changing <see cref="HostPermissionCatalog"/>.
///
/// Outside <c>/api</c>, so it needs no token: it describes what can be granted, never who holds it.
/// </remarks>
[ApiController]
[Route("permissions")]
[NoPermissionRequired]
public class PermissionsController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(Build());

    public static object Build()
    {
        var modules = HostPermissionCatalog.Modules
            .OrderBy(m => m.SortOrder)
            .Select(m => new
            {
                key = m.Key,
                displayName = m.DisplayName,
                sortOrder = m.SortOrder,
                capabilities = m.Capabilities.Select(c => new
                {
                    key = c.Key,
                    displayName = c.DisplayName,
                    description = c.Description,
                    type = "Api",
                }).ToList(),
                nav = m.Nav.OrderBy(n => n.SortOrder).Select(n => new
                {
                    key = n.Key,
                    label = n.Label,
                    iconKey = n.IconKey,
                    routeSegment = n.Key,
                    sortOrder = n.SortOrder,
                    requiredCapability = n.RequiredCapability,
                }).ToList(),
            })
            .ToList();

        return new
        {
            modules,
            capabilities = HostPermissionCatalog.Modules
                .SelectMany(m => m.Capabilities.Select(c => new
                {
                    key = $"{m.Key}.{c.Key}",
                    displayName = $"{m.DisplayName} — {c.DisplayName}",
                }))
                .ToList(),
        };
    }
}

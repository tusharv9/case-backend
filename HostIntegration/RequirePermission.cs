namespace CaseManagement.Api.HostIntegration;

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Declares the permission an endpoint needs. Put it on a controller and override on an action; the most
/// specific one wins. <paramref name="read"/> applies to GET/HEAD/OPTIONS, <paramref name="write"/> to
/// every other verb (defaults to <paramref name="read"/>). A null permission means "any signed-in user".
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequirePermissionAttribute : Attribute
{
    public string? Read { get; }
    public string? Write { get; }

    public RequirePermissionAttribute(string? read, string? write = null)
    {
        Read = read;
        Write = write ?? read;
    }

    public string? For(string httpMethod) =>
        HttpMethods.IsGet(httpMethod) || HttpMethods.IsHead(httpMethod) || HttpMethods.IsOptions(httpMethod) ? Read : Write;
}

/// <summary>
/// The rare endpoint that needs no Case Management permission at all: permission discovery (read by AuthService, before
/// anyone is signed in) and "who am I" (so the screen can tell someone without access that they have none).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class NoPermissionRequiredAttribute : Attribute;

/// <summary>
/// Enforces <see cref="RequirePermissionAttribute"/> on the server — the UI hiding a button is a courtesy, not security.
/// Secure by default: an endpoint that names no permission (or names none for this verb) still needs the caller to hold
/// SOME Case Management permission, because signing in to the OmniConnect Host is not access to Case Management. Only
/// <see cref="NoPermissionRequiredAttribute"/> opts out.
/// </summary>
public sealed class PermissionAuthorizationFilter : IAsyncAuthorizationFilter
{
    internal const string OpenBeforeSignInKey = "cm:open-before-sign-in";

    private readonly ICurrentUserAccessor _user;

    public PermissionAuthorizationFilter(ICurrentUserAccessor user) => _user = user;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<NoPermissionRequiredAttribute>().Any()) return Task.CompletedTask;
        // Standalone development only: the user picker lists users before anyone is signed in (HostIdentityMiddleware).
        if (context.HttpContext.Items.ContainsKey(OpenBeforeSignInKey)) return Task.CompletedTask;

        var attribute = context.ActionDescriptor.EndpointMetadata.OfType<RequirePermissionAttribute>().LastOrDefault();
        var required = attribute?.For(context.HttpContext.Request.Method);
        if (required == null)
        {
            if (_user.Permissions.Count > 0) return Task.CompletedTask;
            context.Result = new ObjectResult(new { error = "You do not have access to Case Management. Ask your administrator to grant it to your role." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return Task.CompletedTask;
        }
        if (_user.Has(required)) return Task.CompletedTask;

        context.Result = new ObjectResult(new { error = "You do not have permission to perform this action.", requiredPermission = required })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
        return Task.CompletedTask;
    }
}

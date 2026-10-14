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

/// <summary>Enforces <see cref="RequirePermissionAttribute"/> on the server — the UI hiding a button is a courtesy, not security.</summary>
public sealed class PermissionAuthorizationFilter : IAsyncAuthorizationFilter
{
    private readonly ICurrentUserAccessor _user;

    public PermissionAuthorizationFilter(ICurrentUserAccessor user) => _user = user;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var attribute = context.ActionDescriptor.EndpointMetadata.OfType<RequirePermissionAttribute>().LastOrDefault();
        var required = attribute?.For(context.HttpContext.Request.Method);
        if (required == null || _user.Has(required)) return Task.CompletedTask;

        context.Result = new ObjectResult(new { error = "You do not have permission to perform this action.", requiredPermission = required })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
        return Task.CompletedTask;
    }
}

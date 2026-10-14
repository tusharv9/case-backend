namespace CaseManagement.Api.HostIntegration;

public sealed class CurrentUserAccessor : ICurrentUserAccessor
{
    internal const string PrincipalKey = "HostPrincipal";
    internal const string PermissionsKey = "HostPermissions";
    internal const string LocalUserIdKey = "UserId"; // existing key read by BaseApiController

    private readonly IHttpContextAccessor _http;
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>();

    public CurrentUserAccessor(IHttpContextAccessor http) => _http = http;

    private IDictionary<object, object?>? Items => _http.HttpContext?.Items;

    public Guid? LocalUserId =>
        Items != null && Items.TryGetValue(LocalUserIdKey, out var v) && v is Guid g ? g : null;

    public HostPrincipal? Principal =>
        Items != null && Items.TryGetValue(PrincipalKey, out var v) ? v as HostPrincipal : null;

    public IReadOnlySet<string> Permissions =>
        Items != null && Items.TryGetValue(PermissionsKey, out var v) && v is IReadOnlySet<string> p ? p : NoPermissions;

    public bool Has(string permission) =>
        Permissions.Contains(HostIntegration.Permissions.Everything) || Permissions.Contains(permission);
}

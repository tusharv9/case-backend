namespace CaseManagement.Api.Extensions;

/// <summary>
/// An explicit listen port for Case, from <c>CASE_PORT</c> only. A generic inherited <c>PORT</c> (a Render
/// leftover, or a variable a parent process set for another app) is deliberately ignored; without
/// <c>CASE_PORT</c> the normal ASP.NET Core settings apply: launchSettings 5110 locally; in the container
/// <c>ASPNETCORE_URLS=http://+:8080</c> (the platform image, Backend/Dockerfile.case) or
/// <c>ASPNETCORE_HTTP_PORTS=8080</c> (this repo's own Dockerfile); or <c>Kestrel:Endpoints</c>.
/// </summary>
public static class PortBinding
{
    public const string Key = "CASE_PORT";

    public static string? ListenUrl(IConfiguration configuration)
    {
        var raw = configuration[Key];
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw.Trim(), out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException($"{Key} must be a port number between 1 and 65535.");
        return $"http://*:{port}";
    }
}

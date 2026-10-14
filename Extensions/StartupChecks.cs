namespace CaseManagement.Api.Extensions;

/// <summary>Loud, early warnings about configuration that is legal but risky in Production.</summary>
public static class StartupChecks
{
    public static void Warn(WebApplication app, string[] allowedOrigins, int originPatternCount)
    {
        if (app.Environment.IsDevelopment()) return;
        var log = app.Logger;

        if (allowedOrigins.Length == 0 && originPatternCount == 0)
            log.LogWarning("No browser origin is allowed (AllowedOrigins is empty): a separately hosted front end will be blocked by CORS. Set AllowedOrigins to its exact origin.");

        var hosts = app.Configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(hosts) || hosts.Trim() == "*")
            log.LogWarning("AllowedHosts is '*': the API answers for any Host header. Set it to the host name(s) this service is served on.");

        if (string.IsNullOrWhiteSpace(app.Configuration["Attachments:StoragePath"]))
            log.LogWarning("Attachments:StoragePath is not set: uploaded files live in the application folder and are lost if the host's file system is ephemeral.");
    }
}

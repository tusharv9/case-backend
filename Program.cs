using CaseManagement.Api.Data;
using CaseManagement.Api.Extensions;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Middleware;
using CaseManagement.Api.Repositories;
using CaseManagement.Api.Services;
using CaseManagement.Api.Services.Strategies;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using CaseManagement.Api.Configuration;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// One JSON object per log line outside Development, so a log aggregator can filter by CorrelationId, route and status.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "O"; o.UseUtcTimestamp = true; });

    // Set in code rather than only in appsettings so a missing or overridden config can never bring back one log line per
    // SQL statement and per framework step. Our own request log line (RequestLoggingMiddleware) carries what is needed.
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
}

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
var securityOptions = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

// Add services to the container.

builder.Services.AddControllers(options =>
    {
        // Enforces [RequirePermission] on the server for every endpoint.
        options.Filters.Add<PermissionAuthorizationFilter>();
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        // A request the framework cannot even bind (a malformed id, a wrong type…) used to reach the user as the raw
        // .NET message ("The JSON value could not be converted to System.Guid. Path: $.fields[8].id …"). The caller gets a
        // plain sentence; the technical detail goes to the log, tied to the correlation id.
        options.InvalidModelStateResponseFactory = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ModelBinding");
            var detail = string.Join(" | ", context.ModelState.Where(e => e.Value?.Errors.Count > 0)
                .SelectMany(e => e.Value!.Errors.Select(x => $"{e.Key}: {x.ErrorMessage}")));
            logger.LogWarning("Request rejected by model binding for {Method} {Path}: {Detail}", context.HttpContext.Request.Method, context.HttpContext.Request.Path, detail);
            var correlationId = context.HttpContext.Items[CaseManagement.Api.Middleware.CorrelationIdMiddleware.Header]?.ToString() ?? string.Empty;
            return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(new
            {
                error = "Some of the information sent was not in the expected format. Reload the page and try again; if it keeps happening, quote the correlation id.",
                correlationId
            });
        };
    })
    .AddJsonOptions(options =>
    {
        // Global string sanitization (trimming) for incoming JSON requests
        options.JsonSerializerOptions.Converters.Add(new TrimStringConverter());
        // Enums as strings
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        // Ignore circular references
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    });

// Dynamic port support for Render / container platforms
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://*:{port}");
}

// CORS allow-list is driven purely by configuration ("AllowedOrigins", comma separated) so each
// environment — and the future Host App's origin — is an explicit entry, never a wildcard.
// In Production there is no implicit default: an empty list means no cross-origin access.
// localhost is allowed only in Development, so a production deployment can never be reached
// from a page running on someone's local machine.
var allowedOriginsRaw = builder.Configuration["AllowedOrigins"]
    ?? (builder.Environment.IsDevelopment() ? "http://localhost:3000,http://localhost:5173" : string.Empty);
var allowedOrigins = allowedOriginsRaw
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(o => o.TrimEnd('/'))
    .ToArray();
var allowLocalhostOrigins = builder.Environment.IsDevelopment();

// Wildcard patterns (e.g. "https://*.example.com") must be asked for, one at a time. They are never a default.
var originPatterns = securityOptions.AllowedOriginPatterns
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(p => OriginPattern.TryParse(p))
    .Where(p => p != null)
    .Cast<OriginPattern>()
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactPolicy", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrEmpty(origin)) return false;
                var trimmed = origin.TrimEnd('/');
                if (allowedOrigins.Any(ao => string.Equals(ao, trimmed, StringComparison.OrdinalIgnoreCase))) return true;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
                if (originPatterns.Any(p => p.Matches(uri))) return true;
                return allowLocalhostOrigins && (uri.Host == "localhost" || uri.Host == "127.0.0.1");
            })
            // Only what the API actually uses. Identity travels in headers (never cookies), so credentialed requests are not allowed.
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "Accept", "X-User-Id", "X-Correlation-Id")
            .WithExposedHeaders("X-Correlation-Id", "Content-Disposition", "X-Content-SHA256", "Retry-After")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
    });
});

// Compress JSON responses (dashboard and lists are the big ones).
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;   // safe here: responses carry no secrets that an attacker can mix with chosen input (no cookies/tokens in bodies)
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});

// Rate limiting: a runaway client or script cannot flatten the API. Limits are per signed-in user (or IP when anonymous).
if (securityOptions.RateLimit.Enabled)
{
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.OnRejected = async (ctx, ct) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                ctx.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();
            ctx.HttpContext.Response.ContentType = "application/json";
            await ctx.HttpContext.Response.WriteAsync("{\"error\":\"Too many requests. Please slow down and try again shortly.\"}", ct);
        };

        static string Caller(HttpContext c) =>
            c.Request.Headers.TryGetValue("X-User-Id", out var u) && !string.IsNullOrWhiteSpace(u) ? "u:" + u.ToString()
            : c.Request.Headers.Authorization.Count > 0 ? "t:" + c.Request.Headers.Authorization.ToString().GetHashCode()
            : "ip:" + (c.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
            !c.Request.Path.StartsWithSegments("/api")
                ? RateLimitPartition.GetNoLimiter("static")
                : RateLimitPartition.GetFixedWindowLimiter(Caller(c), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = securityOptions.RateLimit.RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                }));

        o.AddPolicy("uploads", c => RateLimitPartition.GetFixedWindowLimiter(Caller(c), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = securityOptions.RateLimit.UploadsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
    });
}

// Uploads: the form limit follows the configured maximum file size (plus room for the other fields), not the framework default.
var attachmentMax = builder.Configuration.GetSection(AttachmentOptions.SectionName).Get<AttachmentOptions>()?.MaxFileSizeBytes ?? new AttachmentOptions().MaxFileSizeBytes;
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = attachmentMax + 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = attachmentMax + 2 * 1024 * 1024);

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure PostgreSQL
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Database connection string 'DefaultConnection' was not found. " +
        "Ensure 'ConnectionStrings__DefaultConnection' is configured in your environment variables or appsettings.json.");
}

builder.Services.AddSingleton<IConfigCache, ConfigCache>();
builder.Services.AddSingleton<ConfigChangeInterceptor>();

builder.Services.AddDbContextPool<AppDbContext>((serviceProvider, options) =>
    options
        .UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.CommandTimeout(90);
            npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
        })
        // Any save of configuration data clears the configuration cache (see ConfigChangeInterceptor).
        .AddInterceptors(serviceProvider.GetRequiredService<ConfigChangeInterceptor>()));

// ---- Host App integration -------------------------------------------------------------------
// The Host App owns login, tokens and users. Everything identity-related sits behind the ports in
// HostIntegration/Ports.cs; the mode decides which implementation is used.
var hostSection = builder.Configuration.GetSection(HostIntegrationOptions.SectionName);
var hostOptions = hostSection.Get<HostIntegrationOptions>() ?? new HostIntegrationOptions();
var hostMode = hostOptions.ResolveMode(builder.Environment.IsDevelopment());
builder.Services.Configure<HostIntegrationOptions>(hostSection);
builder.Services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
builder.Services.AddScoped<IUserProjectionService, UserProjectionService>();
builder.Services.AddScoped<IHostUserSynchronizer, HostUserSynchronizer>();

if (hostMode == HostIntegrationMode.Standalone)
{
    if (!builder.Environment.IsDevelopment() && !hostOptions.AllowStandaloneInProduction)
        throw new InvalidOperationException(
            "HostIntegration:Mode is 'Standalone', which has NO real authentication (any caller can name themselves " +
            "with a header). It is only allowed in Development. Set HostIntegration:Mode=Host, or — for a deliberate " +
            "demo deployment — HostIntegration:AllowStandaloneInProduction=true.");

    builder.Services.AddScoped<IHostIdentityResolver, StandaloneIdentityResolver>();
    builder.Services.AddScoped<IPermissionProvider, StandalonePermissionProvider>();
    builder.Services.AddScoped<IHostUserDirectory, LocalHostUserDirectory>();
}
else
{
    var jwt = hostOptions.Jwt;
    if (string.IsNullOrWhiteSpace(jwt.Authority) && string.IsNullOrWhiteSpace(jwt.MetadataAddress) && string.IsNullOrWhiteSpace(jwt.SymmetricKey))
        throw new InvalidOperationException("HostIntegration:Mode=Host needs a way to validate Host tokens: set HostIntegration:Jwt:Authority (or MetadataAddress), or SymmetricKey for local testing.");
    if (string.IsNullOrWhiteSpace(jwt.Audience))
        throw new InvalidOperationException("HostIntegration:Jwt:Audience is required in Host mode, so tokens issued for other applications are rejected.");
    if (!string.IsNullOrWhiteSpace(jwt.SymmetricKey) && Encoding.UTF8.GetByteCount(jwt.SymmetricKey) < 32)
        throw new InvalidOperationException("HostIntegration:Jwt:SymmetricKey must be at least 32 bytes.");

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = false; // keep claim names exactly as the Host issues them (configurable in Claims)
            o.RequireHttpsMetadata = jwt.RequireHttpsMetadata;
            if (!string.IsNullOrWhiteSpace(jwt.Authority)) o.Authority = jwt.Authority;
            if (!string.IsNullOrWhiteSpace(jwt.MetadataAddress)) o.MetadataAddress = jwt.MetadataAddress;

            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = !string.IsNullOrWhiteSpace(jwt.Issuer) || !string.IsNullOrWhiteSpace(jwt.Authority),
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
                NameClaimType = hostOptions.Claims.Name,
                RoleClaimType = hostOptions.Claims.Roles,
                IssuerSigningKey = string.IsNullOrWhiteSpace(jwt.SymmetricKey)
                    ? null
                    : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SymmetricKey))
            };
        });

    builder.Services.AddScoped<IHostIdentityResolver, JwtIdentityResolver>();
    builder.Services.AddScoped<IPermissionProvider, ConfiguredPermissionProvider>();

    if (!string.IsNullOrWhiteSpace(hostOptions.Directory.BaseUrl))
    {
        builder.Services.AddHttpClient<IHostUserDirectory, HttpHostUserDirectory>(c =>
        {
            c.BaseAddress = new Uri(hostOptions.Directory.BaseUrl!.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        if (hostOptions.Directory.SyncIntervalMinutes > 0)
            builder.Services.AddHostedService<HostUserSyncWorker>();
    }
    else
    {
        builder.Services.AddScoped<IHostUserDirectory, NullHostUserDirectory>();
    }
}

// Register Repositories
builder.Services.AddScoped<ICaseRepository, CaseRepository>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IDepartmentRepository, DepartmentRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IConfigurableSettingsRepository, ConfigurableSettingsRepository>();

// Register Services
builder.Services.AddScoped<ICaseService, CaseService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IDepartmentService, DepartmentService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IConfigurableSettingsService, ConfigurableSettingsService>();
builder.Services.AddScoped<IPiiMaskingService, PiiMaskingService>();
builder.Services.AddScoped<IFieldValidationEngine, FieldValidationEngine>();
builder.Services.AddScoped<IMetadataService, MetadataService>();
builder.Services.AddScoped<ICountryService, CountryService>();
builder.Services.AddScoped<IIdFormatService, IdFormatService>();
builder.Services.AddScoped<IFieldTypeChangeChecker, FieldTypeChangeChecker>();
builder.Services.AddScoped<IBusinessTimeService, BusinessTimeService>();
builder.Services.AddScoped<IEscalationService, EscalationService>();
builder.Services.AddScoped<ISlaClockProvider, SlaClockProvider>();
builder.Services.AddScoped<ISlaMonitor, SlaMonitorService>();
builder.Services.Configure<SlaMonitorOptions>(builder.Configuration.GetSection(SlaMonitorOptions.SectionName));
builder.Services.AddScoped<ISlaRoutingService, SlaRoutingService>();
builder.Services.AddScoped<ITeamService, TeamService>();
builder.Services.AddScoped<ITeamMonitoringService, TeamMonitoringService>();
builder.Services.Configure<TeamMonitoringOptions>(builder.Configuration.GetSection(TeamMonitoringOptions.SectionName));
builder.Services.AddScoped<IAssignmentStrategy, RoundRobinAssignmentStrategy>();
builder.Services.AddScoped<IAssignmentStrategy, LeastOccupancyAssignmentStrategy>();
builder.Services.AddScoped<IAssignmentStrategy, SkillBasedAssignmentStrategy>();
builder.Services.Configure<CaseManagement.Api.Configuration.NotificationOptions>(builder.Configuration.GetSection(CaseManagement.Api.Configuration.NotificationOptions.SectionName));
builder.Services.AddSingleton<IAttachmentStore, LocalDiskAttachmentStore>();
builder.Services.AddScoped<IAttachmentService, AttachmentService>();
builder.Services.AddScoped<IMentionService, MentionService>();
builder.Services.AddScoped<ICaseCollaborationService, CaseCollaborationService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IAssignmentEligibilityService, AssignmentEligibilityService>();
builder.Services.AddScoped<IAgentPoolService>(sp => new AgentPoolService(sp.GetRequiredService<IAssignmentEligibilityService>()));
builder.Services.AddScoped<ISkillService, SkillService>();
builder.Services.AddScoped<IRoutingEngineService, RoutingEngineService>();
builder.Services.AddScoped<IAssignmentSettingsService, AssignmentSettingsService>();

// Database preparation (migrations + seed) runs in the background; the worker below waits for it.
builder.Services.Configure<CaseManagement.Api.Configuration.DatabaseOptions>(
    builder.Configuration.GetSection(CaseManagement.Api.Configuration.DatabaseOptions.SectionName));
builder.Services.AddSingleton<DatabaseInitializationState>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseInitializer>());

// Register SLA Escalation Background Worker
builder.Services.AddHostedService<SlaEscalationBackgroundService>();

// In-process cache used by the authorization middleware and lookup caching
builder.Services.AddMemoryCache();

// Lets services read the acting user (for audit records) set by UserAuthorizationMiddleware
builder.Services.AddHttpContextAccessor();

// Bind tunables so they are configurable per environment rather than compiled in
builder.Services.Configure<CaseManagement.Api.Configuration.SearchOptions>(
    builder.Configuration.GetSection(CaseManagement.Api.Configuration.SearchOptions.SectionName));
builder.Services.Configure<CaseManagement.Api.Configuration.LookupCacheOptions>(
    builder.Configuration.GetSection(CaseManagement.Api.Configuration.LookupCacheOptions.SectionName));
builder.Services.Configure<CaseManagement.Api.Configuration.AttachmentOptions>(
    builder.Configuration.GetSection(CaseManagement.Api.Configuration.AttachmentOptions.SectionName));

// Register FluentValidation
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

var app = builder.Build();

// Order matters: correlation id first (so everything after it, including errors, can quote it), then the safety net.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseResponseCompression();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<ExceptionMiddleware>();

// Configure the HTTP request pipeline.
// Swagger exposes the full API surface, so it is off outside Development unless explicitly enabled.
if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("EnableSwagger", false))
{
    app.UseSwagger();
    app.UseSwaggerUI();
    if (!app.Environment.IsDevelopment())
        app.Logger.LogWarning("Swagger is ENABLED outside Development (EnableSwagger=true): the whole API surface is browsable.");
}

StartupChecks.Warn(app, allowedOrigins, originPatterns.Length);

// NOTE: schema creation/upgrade and seeding no longer happen inline here. They are handled by
// DatabaseInitializer (EF migrations + one-time seed steps), which runs in the background so the
// process answers /health immediately, and can be run as a deployment step:
//     dotnet CaseManagement.Api.dll --migrate-only
if (args.Contains("--migrate-only"))
{
    var initialised = await app.Services
        .GetRequiredService<DatabaseInitializer>()
        .RunAsync(forceMigrate: true, CancellationToken.None);
    app.Logger.LogInformation(initialised ? "Migration finished." : "Migration FAILED.");
    return initialised ? 0 : 1;
}

// One-off data reconciliation (report by default; --apply to fix). Never migrates: the schema must already be current.
//     dotnet CaseManagement.Api.dll --reconcile-data [--apply --actor <email>] [--report <file.json>]
if (args.Contains("--reconcile-data"))
{
    string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        Console.Error.WriteLine($"The database is not at the current schema ({pending.Count} pending migration(s): {string.Join(", ", pending)}). Run --migrate-only first.");
        return 2;
    }
    var apply = args.Contains("--apply");
    Guid? actor = null;
    if (apply)
    {
        var email = Arg("--actor");
        actor = email == null ? null : await db.Users.Where(u => u.Email == email && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
        if (actor == null) { Console.Error.WriteLine("--apply needs --actor <email of an active user>; the audit events name who made the change."); return 2; }
    }
    var report = await new DataReconciliationService(db, scope.ServiceProvider.GetRequiredService<IRoutingEngineService>()).RunAsync(apply, actor);
    Console.WriteLine(DataReconciliationService.ToText(report));
    if (Arg("--report") is { } path)
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// One-off reconciliation with the business workbook (see WorkbookReconciliationService):
//     dotnet CaseManagement.Api.dll --reconcile-workbook <folder with the worksheet CSVs> --decisions <file.json> [--apply --actor <email>] [--report <file.json>]
if (args.Contains("--reconcile-workbook"))
{
    string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        Console.Error.WriteLine($"The database is not at the current schema ({pending.Count} pending migration(s): {string.Join(", ", pending)}). Run --migrate-only first.");
        return 2;
    }
    if (Arg("--reconcile-workbook") is not { } folder || Arg("--decisions") is not { } decisionsPath)
    {
        Console.Error.WriteLine("Usage: --reconcile-workbook <folder with the worksheet CSVs> --decisions <file.json> [--apply --actor <email>] [--report <file.json>]");
        return 2;
    }
    var apply = args.Contains("--apply");
    Guid? actor = null;
    if (apply)
    {
        var email = Arg("--actor");
        actor = email == null ? null : await db.Users.Where(u => u.Email == email && u.IsActive).Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
        if (actor == null) { Console.Error.WriteLine("--apply needs --actor <email of an active user>; the audit events name who made the change."); return 2; }
    }
    try
    {
        var workbook = WorkbookReconciliationService.ReadWorkbook(folder);
        var decisions = WorkbookReconciliationService.ReadDecisions(decisionsPath);
        var report = await new WorkbookReconciliationService(db).RunAsync(workbook, decisions, apply, actor);
        Console.WriteLine(WorkbookReconciliationService.ToText(report));
        if (Arg("--report") is { } path)
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Console.Error.WriteLine("Nothing was written.");
        return 1;
    }
    return 0;
}

// Forward headers from reverse proxies (Render, load balancers)
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                       Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseCors("ReactPolicy");
if (securityOptions.RateLimit.Enabled) app.UseRateLimiter();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Before anything that touches the database: API calls get a clean 503 until it is ready.
app.UseMiddleware<DatabaseReadinessMiddleware>();
if (hostMode == HostIntegrationMode.Host) app.UseAuthentication();   // validates the Host-issued JWT
app.UseMiddleware<HostIdentityMiddleware>();
app.UseAuthorization();

// Liveness: the process is up (answers immediately, even while the database is still being prepared).
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Readiness: the database is migrated/seeded and the API can serve requests.
app.MapGet("/ready", (DatabaseInitializationState state) =>
    state.IsReady
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = state.Status.ToString(), message = state.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapControllers();

app.Run();

return 0;

// Makes the entry point visible to WebApplicationFactory in integration tests.
public partial class Program { }

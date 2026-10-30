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

// Load .env before configuration is read — the OmniConnect convention for secrets such as the platform's
// Jwt__SigningKeyPublic, so none of them is committed to appsettings. Only the working directory (dotnet run
// from this folder) and the output folder are searched: never parent folders, so a test run is not
// silently reconfigured by a developer's .env. A variable already set by whoever started the process wins over the
// file (NoClobber): .env fills in local defaults, it never overrides an explicit setting.
foreach (var envPath in new[] { Path.Combine(Directory.GetCurrentDirectory(), ".env"), Path.Combine(AppContext.BaseDirectory, ".env") })
{
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.NoClobber().Load(envPath);
        break;
    }
}

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

// An explicit port for Case only (CASE_PORT). An inherited PORT meant for another app is ignored.
var caseListenUrl = PortBinding.ListenUrl(builder.Configuration);
if (caseListenUrl != null)
{
    builder.WebHost.UseUrls(caseListenUrl);
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

// Keep a few connections open and alive (the same rule the platform services use, AuthService WithWarmPool): every new
// connection to the database costs a TLS handshake of several round trips, which the dashboard's side-by-side reads would
// otherwise pay on the first load after a quiet spell. Database:MinimumPoolSize overrides the default of 10.
{
    var npgsql = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
    var minimum = builder.Configuration.GetValue<int?>("Database:MinimumPoolSize") ?? 10;
    if (npgsql.MinPoolSize == 0 && minimum > 0) npgsql.MinPoolSize = Math.Min(minimum, npgsql.MaxPoolSize);
    if (npgsql.KeepAlive == 0) npgsql.KeepAlive = 30;
    connectionString = npgsql.ConnectionString;
}

builder.Services.AddSingleton<IConfigCache, ConfigCache>();
builder.Services.AddSingleton<ConfigChangeInterceptor>();

// Pooled contexts, handed out two ways: one per request (the usual AppDbContext), and on demand through
// IDbContextFactory for work that runs independent queries side by side (the dashboard), each on its own
// connection — every round trip to the database costs the same however small the query, so waiting for ten of
// them one after another is what made the dashboard slow.
builder.Services.AddPooledDbContextFactory<AppDbContext>((serviceProvider, options) =>
    options
        .UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.CommandTimeout(90);
            npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
        })
        // Any save of configuration data clears the configuration cache (see ConfigChangeInterceptor).
        .AddInterceptors(serviceProvider.GetRequiredService<ConfigChangeInterceptor>()));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

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
    // The OmniConnect platform's own keys (Jwt__SigningKeyPublic / Jwt__Issuer / Jwt__Audience — the same
    // values every service's .env carries) are used when the HostIntegration ones are not set.
    var platformJwt = builder.Configuration.GetSection("Jwt");
    if (string.IsNullOrWhiteSpace(jwt.SigningKeyPublic)) jwt.SigningKeyPublic = platformJwt["SigningKeyPublic"];
    if (string.IsNullOrWhiteSpace(jwt.Issuer)) jwt.Issuer = platformJwt["Issuer"];
    if (string.IsNullOrWhiteSpace(jwt.Audience)) jwt.Audience = platformJwt["Audience"];

    HostJwtValidation.EnsureConfigured(jwt, builder.Environment.IsDevelopment());

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.MapInboundClaims = false; // keep claim names exactly as the Host issues them (configurable in Claims)
            o.RequireHttpsMetadata = jwt.RequireHttpsMetadata;
            if (!string.IsNullOrWhiteSpace(jwt.Authority)) o.Authority = jwt.Authority;
            if (!string.IsNullOrWhiteSpace(jwt.MetadataAddress)) o.MetadataAddress = jwt.MetadataAddress;

            o.TokenValidationParameters = HostJwtValidation.Parameters(jwt, hostOptions.Claims);
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
builder.Services.AddScoped<ICaseAccessScope, CaseAccessScope>();

// The Host's user directory, read with the caller's token, for choosing team members (HostIntegration/HostUsers.cs).
builder.Services.Configure<AuthServiceOptions>(builder.Configuration.GetSection(AuthServiceOptions.SectionName));
builder.Services.AddHttpClient<HostUserDirectoryClient>();
builder.Services.AddHttpClient<HostSignInHistoryClient>();
builder.Services.AddScoped<HostUserService>();

// Customer 360: existing customers are read from it with the caller's own token, and a case keeps only their
// identifiers — encrypted, plus a keyed hash for lookups (Services/Customer360).
builder.Services.Configure<CaseManagement.Api.Services.Customer360.Customer360Options>(
    builder.Configuration.GetSection(CaseManagement.Api.Services.Customer360.Customer360Options.SectionName));
builder.Services.Configure<CaseManagement.Api.Services.Customer360.CustomerIdentifierOptions>(
    builder.Configuration.GetSection(CaseManagement.Api.Services.Customer360.CustomerIdentifierOptions.SectionName));
builder.Services.AddSingleton<CaseManagement.Api.Services.Customer360.ICustomerIdentifierProtector, CaseManagement.Api.Services.Customer360.CustomerIdentifierProtector>();
builder.Services.AddHttpClient<CaseManagement.Api.Services.Customer360.Customer360Client>();
builder.Services.AddScoped<CaseManagement.Api.Services.Customer360.ICustomer360CustomerService, CaseManagement.Api.Services.Customer360.Customer360CustomerService>();
builder.Services.AddScoped<CaseManagement.Api.Services.Customer360.ICaseCustomerNames, CaseManagement.Api.Services.Customer360.CaseCustomerNames>();
builder.Services.AddSingleton<CaseManagement.Api.Services.Customer360.ICustomer360Handoff, CaseManagement.Api.Services.Customer360.Customer360Handoff>();
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
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<ITeamSettingsService, TeamSettingsService>();
builder.Services.AddScoped<IOrgDepartmentService, OrgDepartmentService>();
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

// One-off: team membership back to Host users only (see TeamMembershipResetService). The Host user ids come from the
// Host's own database, one per line, so no person is judged "not a Host user" by guesswork:
//     dotnet CaseManagement.Api.dll --reset-team-membership --host-users <file> [--apply --actor <email>]
if (args.Contains("--reset-team-membership"))
{
    string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        Console.Error.WriteLine($"The database is not at the current schema ({pending.Count} pending migration(s)). Run --migrate-only first.");
        return 2;
    }
    if (Arg("--host-users") is not { } hostFile || !File.Exists(hostFile))
    {
        Console.Error.WriteLine("--host-users <file> is required: the Host user ids, one per line.");
        return 2;
    }
    var hostIds = (await File.ReadAllLinesAsync(hostFile)).Select(l => l.Trim()).Where(l => Guid.TryParse(l, out _))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var apply = args.Contains("--apply");
    Guid? actor = null;
    if (apply)
    {
        var email = Arg("--actor");
        actor = email == null ? null : await db.Users.Where(u => u.Email == email && u.IsActive && u.ExternalUserId != null)
            .Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
        if (actor == null) { Console.Error.WriteLine("--apply needs --actor <email of an active Host user>; the audit events name who made the change."); return 2; }
    }
    try
    {
        var report = await new TeamMembershipResetService(db).RunAsync(hostIds, apply, actor);
        Console.WriteLine(TeamMembershipResetService.ToText(report));
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
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

// The OmniConnect edge (nginx in production, the host dev server locally) publishes this service under
// its own prefix. UsePathBase also accepts the unprefixed path, so the standalone frontend's /api proxy,
// server-to-server callers and /health keep working unchanged.
var pathBase = (app.Configuration["Hosting:PathBase"] ?? "/api/casemanagement-service").Trim().TrimEnd('/');
if (pathBase.Length > 0)
{
    if (!pathBase.StartsWith('/') || pathBase.Contains("//") || pathBase.Contains('?'))
        throw new InvalidOperationException($"Hosting:PathBase '{pathBase}' is not a plain path such as /api/casemanagement-service.");
    app.UsePathBase(pathBase);
}

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

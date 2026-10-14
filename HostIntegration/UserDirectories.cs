namespace CaseManagement.Api.HostIntegration;

using System.Net.Http.Headers;
using System.Text.Json;
using CaseManagement.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>STANDALONE: the local users table (the development sample users) is the directory.</summary>
public sealed class LocalHostUserDirectory : IHostUserDirectory
{
    private readonly AppDbContext _db;

    public LocalHostUserDirectory(AppDbContext db) => _db = db;

    public async Task<HostUserInfo?> GetAsync(string externalUserId, CancellationToken ct) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.ExternalUserId == externalUserId)
            .Select(u => new HostUserInfo(u.ExternalUserId!, u.Name, u.Email, u.Role, u.IsActive))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<HostUserInfo>> ListAsync(CancellationToken ct) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.ExternalUserId != null)
            .Select(u => new HostUserInfo(u.ExternalUserId!, u.Name, u.Email, u.Role, u.IsActive))
            .ToListAsync(ct);
}

/// <summary>HOST without a configured directory: users appear lazily from their token claims only.</summary>
public sealed class NullHostUserDirectory : IHostUserDirectory
{
    public Task<HostUserInfo?> GetAsync(string externalUserId, CancellationToken ct) => Task.FromResult<HostUserInfo?>(null);
    public Task<IReadOnlyList<HostUserInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostUserInfo>>(Array.Empty<HostUserInfo>());
}

/// <summary>
/// HOST: reads the Host's user-directory API. Endpoint paths, response shape (array or wrapped), the
/// property names of a user and the service credential are configuration (HostIntegration:Directory).
/// </summary>
public sealed class HttpHostUserDirectory : IHostUserDirectory
{
    private readonly HttpClient _http;
    private readonly HostIntegrationOptions.DirectorySettings _settings;
    private readonly ILogger<HttpHostUserDirectory> _logger;

    public HttpHostUserDirectory(HttpClient http, IOptions<HostIntegrationOptions> options, ILogger<HttpHostUserDirectory> logger)
    {
        _http = http;
        _settings = options.Value.Directory;
        _logger = logger;
    }

    public async Task<HostUserInfo?> GetAsync(string externalUserId, CancellationToken ct)
    {
        var path = _settings.GetPath.Replace("{id}", Uri.EscapeDataString(externalUserId));
        using var request = NewRequest(path);
        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Map(doc.RootElement);
    }

    public async Task<IReadOnlyList<HostUserInfo>> ListAsync(CancellationToken ct)
    {
        using var request = NewRequest(_settings.ListPath);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (!string.IsNullOrWhiteSpace(_settings.ItemsProperty))
        {
            if (!root.TryGetProperty(_settings.ItemsProperty, out root))
                throw new InvalidOperationException($"Host directory response has no '{_settings.ItemsProperty}' property.");
        }

        var users = new List<HostUserInfo>();
        foreach (var item in root.EnumerateArray())
        {
            var user = Map(item);
            if (user != null) users.Add(user);
        }
        return users;
    }

    private HttpRequestMessage NewRequest(string path)
    {
        // Relative on purpose: a leading '/' would replace the base address' path (e.g. "/api/") instead of extending it.
        var request = new HttpRequestMessage(HttpMethod.Get, path.TrimStart('/'));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            request.Headers.TryAddWithoutValidation(_settings.ApiKeyHeader, _settings.ApiKey);
        return request;
    }

    private HostUserInfo? Map(JsonElement element)
    {
        var f = _settings.Fields;
        var id = Text(element, f.Id);
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Skipping a directory user without an '{Field}' property.", f.Id);
            return null;
        }

        var isActive = element.TryGetProperty(f.IsActive, out var active) && active.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? active.GetBoolean()
            : true;

        return new HostUserInfo(id, Text(element, f.Name) ?? id, Text(element, f.Email), Text(element, f.Role), isActive);
    }

    // The id may be a string or a number in the Host's API; both become an opaque string.
    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;
}

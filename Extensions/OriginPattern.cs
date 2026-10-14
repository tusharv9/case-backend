namespace CaseManagement.Api.Extensions;

/// <summary>
/// An allowed-origin pattern such as <c>https://*.example.com</c>: the scheme must match, and the host must be the given domain or a
/// subdomain of it (the wildcard is one leading label and is never "anything").
/// </summary>
public sealed class OriginPattern
{
    private readonly string _scheme;
    private readonly string _domain;     // "example.com"
    private readonly int? _port;

    private OriginPattern(string scheme, string domain, int? port) { _scheme = scheme; _domain = domain; _port = port; }

    public static OriginPattern? TryParse(string pattern)
    {
        var parts = pattern.Split("://", 2);
        if (parts.Length != 2 || parts[0] is not ("http" or "https")) return null;

        var hostPort = parts[1].TrimEnd('/');
        if (!hostPort.StartsWith("*.")) return null;               // only a single leading wildcard label is supported

        var rest = hostPort[2..];
        if (rest.Contains('*')) return null;                         // exactly one wildcard, at the front
        int? port = null;
        var colon = rest.LastIndexOf(':');
        if (colon > 0 && int.TryParse(rest[(colon + 1)..], out var p)) { port = p; rest = rest[..colon]; }

        // "*.com" or "*.localhost" would open the door to the world; a registrable domain needs at least two labels.
        if (rest.Split('.', StringSplitOptions.RemoveEmptyEntries).Length < 2) return null;
        return new OriginPattern(parts[0], rest.ToLowerInvariant(), port);
    }

    public bool Matches(Uri origin)
    {
        if (!string.Equals(origin.Scheme, _scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (_port.HasValue && origin.Port != _port.Value) return false;
        var host = origin.Host.ToLowerInvariant();
        return host.EndsWith("." + _domain, StringComparison.Ordinal);   // subdomains only; the bare domain is listed as an exact origin
    }
}

namespace CaseManagement.Api.Configuration;

/// <summary>Web-facing protections. Every default is the safe one.</summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Extra browser origins allowed by PATTERN, comma separated, e.g. <c>https://*.example.com</c>. A pattern must name the scheme and may
    /// use ONE leading wildcard label. Empty by default: allowing every preview deployment of a hosting provider is a deliberate choice,
    /// not a default. (Exact origins go in the top-level <c>AllowedOrigins</c> setting.)
    /// </summary>
    public string AllowedOriginPatterns { get; set; } = string.Empty;

    /// <summary>Send <c>Strict-Transport-Security</c> (HTTPS only, outside Development).</summary>
    public bool EnableHsts { get; set; } = true;

    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>Requests slower than this are logged as warnings.</summary>
    public int SlowRequestMilliseconds { get; set; } = 2000;
}

public class RateLimitOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>All API calls per caller (signed-in user, otherwise IP address) per minute.</summary>
    public int RequestsPerMinute { get; set; } = 600;

    /// <summary>File uploads per caller per minute.</summary>
    public int UploadsPerMinute { get; set; } = 30;
}

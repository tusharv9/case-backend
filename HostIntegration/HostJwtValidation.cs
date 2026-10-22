namespace CaseManagement.Api.HostIntegration;

using System.Text;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Host-mode token validation: the startup checks and the <see cref="TokenValidationParameters"/>, kept
/// out of Program.cs so they can be tested without booting the app.
/// </summary>
public static class HostJwtValidation
{
    /// <summary>Refuses to start with a configuration that would accept tokens it should not.</summary>
    public static void EnsureConfigured(HostIntegrationOptions.JwtSettings jwt, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(jwt.Authority) && string.IsNullOrWhiteSpace(jwt.MetadataAddress)
            && string.IsNullOrWhiteSpace(jwt.SigningKeyPublic) && string.IsNullOrWhiteSpace(jwt.SymmetricKey))
            throw new InvalidOperationException("HostIntegration:Mode=Host needs a way to validate Host tokens: set Jwt:SigningKeyPublic (the OmniConnect platform public key), HostIntegration:Jwt:Authority (or MetadataAddress), or SymmetricKey for local testing.");

        // Outside Development the issuer is always named explicitly, so a token from any other issuer is
        // rejected whatever the discovery document says. Development may take it from the authority's metadata.
        if (string.IsNullOrWhiteSpace(jwt.Issuer)
            && (!isDevelopment || (string.IsNullOrWhiteSpace(jwt.Authority) && string.IsNullOrWhiteSpace(jwt.MetadataAddress))))
            throw new InvalidOperationException("HostIntegration:Jwt:Issuer (or Jwt:Issuer) is required in Host mode, so tokens from another issuer are rejected. Set it to the OmniConnect AuthService issuer.");

        if (string.IsNullOrWhiteSpace(jwt.Audience))
            throw new InvalidOperationException("HostIntegration:Jwt:Audience is required in Host mode, so tokens issued for other applications are rejected.");
        if (!string.IsNullOrWhiteSpace(jwt.SymmetricKey) && Encoding.UTF8.GetByteCount(jwt.SymmetricKey) < 32)
            throw new InvalidOperationException("HostIntegration:Jwt:SymmetricKey must be at least 32 bytes.");
    }

    public static TokenValidationParameters Parameters(HostIntegrationOptions.JwtSettings jwt, HostIntegrationOptions.ClaimSettings claims)
    {
        var parameters = new TokenValidationParameters
        {
            // Always on. With no explicit ValidIssuer, JwtBearer validates against the issuer in the
            // Authority/MetadataAddress discovery document.
            ValidateIssuer = true,
            ValidIssuer = string.IsNullOrWhiteSpace(jwt.Issuer) ? null : jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
            NameClaimType = claims.Name,
            RoleClaimType = claims.Roles,
            IssuerSigningKey = !string.IsNullOrWhiteSpace(jwt.SigningKeyPublic)
                ? new RsaSecurityKey(RsaPublicKey.Load(jwt.SigningKeyPublic))
                : string.IsNullOrWhiteSpace(jwt.SymmetricKey)
                    ? null
                    : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SymmetricKey))
        };

        // An RS256 platform key accepts RS256 only — never an HMAC token signed with the public key's bytes.
        // A symmetric test key accepts HS256 only.
        if (!string.IsNullOrWhiteSpace(jwt.SigningKeyPublic))
            parameters.ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 };
        else if (!string.IsNullOrWhiteSpace(jwt.SymmetricKey))
            parameters.ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 };

        return parameters;
    }
}

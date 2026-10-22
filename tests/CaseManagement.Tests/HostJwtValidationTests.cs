using CaseManagement.Api.HostIntegration;
using Microsoft.IdentityModel.Tokens;

namespace CaseManagement.Tests;

/// <summary>
/// Host-mode token validation is the only thing standing between Case and a token minted by someone else,
/// so the issuer check must not depend on which of the optional settings happen to be present.
/// </summary>
/// <remarks>
/// Before this, issuer validation was switched on only when an Issuer or Authority was configured: a
/// deployment that named only a MetadataAddress accepted any issuer, and a production deployment could
/// start without naming an issuer at all.
/// </remarks>
public class HostJwtValidationTests
{
    private const string Key = "unit-test-signing-key-which-is-long-enough-32b!";

    private static HostIntegrationOptions.JwtSettings Jwt(string? issuer = "https://host.example", string? authority = null, string? metadata = null) => new()
    {
        Issuer = issuer,
        Authority = authority,
        MetadataAddress = metadata,
        Audience = "case-management",
        SymmetricKey = Key,
    };

    [Fact]
    public void A_production_deployment_without_an_issuer_refuses_to_start_and_says_which_setting_is_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            HostJwtValidation.EnsureConfigured(Jwt(issuer: null, authority: "https://login.example"), isDevelopment: false));

        Assert.Contains("Jwt:Issuer", ex.Message);
    }

    [Fact]
    public void Development_may_rely_on_the_authority_to_supply_the_issuer()
    {
        HostJwtValidation.EnsureConfigured(Jwt(issuer: null, authority: "https://login.example"), isDevelopment: true);
    }

    [Fact]
    public void A_configuration_with_no_issuer_source_at_all_is_refused_even_in_development()
    {
        Assert.Throws<InvalidOperationException>(() => HostJwtValidation.EnsureConfigured(Jwt(issuer: null), isDevelopment: true));
    }

    [Theory]
    [InlineData("https://host.example", null, null)]
    [InlineData(null, "https://login.example", null)]
    [InlineData(null, null, "https://login.example/.well-known/openid-configuration")]
    public void The_issuer_is_always_validated_whichever_setting_supplies_it(string? issuer, string? authority, string? metadata)
    {
        var parameters = HostJwtValidation.Parameters(Jwt(issuer, authority, metadata), new HostIntegrationOptions.ClaimSettings());

        Assert.True(parameters.ValidateIssuer);
        Assert.Equal(issuer, parameters.ValidIssuer);
    }

    [Fact]
    public void A_symmetric_test_key_accepts_HS256_only()
    {
        var parameters = HostJwtValidation.Parameters(Jwt(), new HostIntegrationOptions.ClaimSettings());

        Assert.Equal(new[] { SecurityAlgorithms.HmacSha256 }, parameters.ValidAlgorithms);
    }

    [Fact]
    public void Audience_lifetime_and_signing_key_are_always_validated()
    {
        var parameters = HostJwtValidation.Parameters(Jwt(), new HostIntegrationOptions.ClaimSettings());

        Assert.True(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.RequireExpirationTime);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.Equal("case-management", parameters.ValidAudience);
    }
}

using CaseManagement.Api.Extensions;
using Microsoft.Extensions.Configuration;

namespace CaseManagement.Tests;

/// <summary>
/// Which port Case listens on must be Case's own decision, not whatever a parent process left behind.
/// </summary>
/// <remarks>
/// Case used to bind to any inherited <c>PORT</c> (a Render leftover). Started from a shell or a dev script
/// that had <c>PORT</c> set for another app, it silently took that app's port instead of 5110.
/// </remarks>
public class PortBindingTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void An_inherited_PORT_is_ignored()
    {
        Assert.Null(PortBinding.ListenUrl(Config(("PORT", "3000"))));
    }

    [Fact]
    public void CASE_PORT_binds_every_interface_on_that_port()
    {
        Assert.Equal("http://*:5110", PortBinding.ListenUrl(Config(("PORT", "3000"), ("CASE_PORT", "5110"))));
    }

    [Fact]
    public void Nothing_configured_leaves_the_normal_ASP_NET_Core_binding_alone()
    {
        Assert.Null(PortBinding.ListenUrl(Config()));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("70000")]
    public void A_CASE_PORT_that_is_not_a_port_number_fails_startup_with_its_name(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PortBinding.ListenUrl(Config(("CASE_PORT", value))));
        Assert.Contains("CASE_PORT", ex.Message);
    }
}

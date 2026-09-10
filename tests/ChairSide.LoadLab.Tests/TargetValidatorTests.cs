using ChairSide.LoadLab;

namespace ChairSide.LoadLab.Tests;

public sealed class TargetValidatorTests
{
    // -- localhost / 127.0.0.1 are always accepted --

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://127.0.0.1")]
    public void Localhost_and_loopback_are_accepted(string url)
    {
        var result = TargetValidator.Validate(url, allowProductionTarget: false);
        Assert.Null(result);
    }

    // -- production hostname "chairside" is blocked by default --

    [Theory]
    [InlineData("http://chairside")]
    [InlineData("http://chairside/")]
    [InlineData("http://chairside:80")]
    [InlineData("https://chairside")]
    public void Chairside_host_is_refused(string url)
    {
        var result = TargetValidator.Validate(url, allowProductionTarget: false);
        Assert.NotNull(result);
        Assert.Contains("chairside", result, StringComparison.OrdinalIgnoreCase);
    }

    // -- non-localhost / non-chairside hosts are also blocked by default --

    [Theory]
    [InlineData("http://10.0.0.1:5000")]
    [InlineData("http://192.168.1.100:5000")]
    [InlineData("https://myserver.example.com")]
    [InlineData("http://192.168.0.50")]
    public void Non_localhost_host_is_refused_without_override(string url)
    {
        var result = TargetValidator.Validate(url, allowProductionTarget: false);
        Assert.NotNull(result);
        // Message should mention "localhost" to tell the user what is safe.
        Assert.Contains("localhost", result, StringComparison.OrdinalIgnoreCase);
    }

    // -- allow-production-target overrides all refusals --

    [Theory]
    [InlineData("http://chairside/")]
    [InlineData("http://10.0.0.1:5000")]
    [InlineData("https://myserver.example.com")]
    [InlineData("http://192.168.1.100")]
    public void Allow_production_target_overrides_any_refusal(string url)
    {
        var result = TargetValidator.Validate(url, allowProductionTarget: true);
        Assert.Null(result);
    }

    // Localhost is still accepted even when the override is true.
    [Fact]
    public void Localhost_is_accepted_with_override_flag_as_well()
    {
        var result = TargetValidator.Validate("http://localhost:5000", allowProductionTarget: true);
        Assert.Null(result);
    }

    // -- malformed URLs are rejected cleanly --

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData("just-a-hostname")]
    public void Malformed_url_returns_error_message(string url)
    {
        var result = TargetValidator.Validate(url, allowProductionTarget: false);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void Malformed_url_error_is_also_suppressed_when_override_is_true(string url)
    {
        // Malformed URLs are always rejected - the override does not bypass URL parsing.
        var result = TargetValidator.Validate(url, allowProductionTarget: true);
        Assert.NotNull(result);
    }
}

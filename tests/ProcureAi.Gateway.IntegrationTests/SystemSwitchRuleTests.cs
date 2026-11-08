using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using ProcureAi.Gateway.Bff;
using ProcureAi.Gateway.Configuration;

namespace ProcureAi.Gateway.IntegrationTests;

/// <summary>
/// Plan 08 Task 12b: which paths the system switch refuses while the demo is off (<see cref="SystemAvailabilityMiddleware.Applies"/>) and which
/// <c>SystemAvailability</c> settings start-up refuses. No host needed.
/// </summary>
public sealed class SystemSwitchRuleTests
{
    private static readonly SystemAvailabilityOptions Configured = new()
    {
        BlockedPaths = ["/api", "/bff", "/hubs"],
        AllowedPaths = ["/api/v1/system/availability", "/bff/logout", "/bff/logout-all"],
    };

    [Theory]
    [InlineData("/api/v1/requests")]
    [InlineData("/API/v1/requests")]
    [InlineData("/Api/V1/Requests/PR-1")]
    [InlineData("/api")]
    [InlineData("/bff/user")]
    [InlineData("/BFF/user")]
    [InlineData("/Bff/Login")]
    [InlineData("/hubs/notifications")]
    [InlineData("/HUBS/notifications/negotiate")]
    [InlineData("/api/v1/system")]
    [InlineData("/api/v1/system/other")]
    public void Refused_while_off(string path) =>
        SystemAvailabilityMiddleware.Applies(new PathString(path), Configured).ShouldBeTrue(path);

    [Theory]
    [InlineData("/api/v1/system/availability")]
    [InlineData("/API/V1/SYSTEM/AVAILABILITY")]
    [InlineData("/Api/v1/System/Availability")]
    [InlineData("/")]
    [InlineData("/config.json")]
    [InlineData("/health/live")]
    [InlineData("/swagger/index.html")]
    [InlineData("/maintenance")]
    [InlineData("/main-ABC123.js")]
    [InlineData("/apis")]
    [InlineData("/bffx/user")]
    [InlineData("/hubsy")]
    [InlineData("/bff/logout")]
    [InlineData("/bff/logout-all")]
    public void Passes_while_off(string path) =>
        SystemAvailabilityMiddleware.Applies(new PathString(path), Configured).ShouldBeFalse(path);

    [Fact]
    public void The_shipped_settings_are_valid() => Errors(Configured).ShouldBeEmpty();

    [Theory]
    [InlineData("/")]
    [InlineData("/config.json")]
    [InlineData("/health")]
    [InlineData("/Health")]
    [InlineData("/swagger")]
    public void A_blocked_path_over_what_must_stay_reachable_is_refused(string blocked) =>
        Errors(new SystemAvailabilityOptions { BlockedPaths = ["/api", blocked], AllowedPaths = ["/api/v1/system/availability"] })
            .ShouldNotBeEmpty(blocked);

    [Theory]
    [InlineData("/api/v1/system")]
    [InlineData("/api/v1")]
    public void Blocking_the_switch_itself_is_refused_unless_it_is_allowed_again(string blocked)
    {
        Errors(new SystemAvailabilityOptions { BlockedPaths = [blocked] }).ShouldNotBeEmpty();
        Errors(new SystemAvailabilityOptions { BlockedPaths = [blocked], AllowedPaths = ["/api/v1/system/availability"] }).ShouldBeEmpty();
    }

    [Fact]
    public void The_api_without_the_switch_allowed_again_is_refused() =>
        Errors(new SystemAvailabilityOptions { BlockedPaths = ["/api", "/bff"] }).ShouldNotBeEmpty();

    [Theory]
    [InlineData("api")]
    [InlineData("/api/")]
    [InlineData(" ")]
    public void Malformed_paths_are_refused(string path) =>
        Errors(new SystemAvailabilityOptions { BlockedPaths = ["/bff", path], AllowedPaths = [] }).ShouldNotBeEmpty(path);

    [Fact]
    public void An_allowed_path_outside_every_blocked_path_is_refused() =>
        Errors(new SystemAvailabilityOptions { BlockedPaths = ["/bff"], AllowedPaths = ["/api/v1/system/availability"] }).ShouldNotBeEmpty();

    [Fact]
    public void No_blocked_path_at_all_is_refused() =>
        Errors(new SystemAvailabilityOptions { BlockedPaths = [] }).ShouldNotBeEmpty();

    [Fact]
    public void A_token_timeout_out_of_range_is_refused() =>
        Errors(new SystemAvailabilityOptions
        {
            BlockedPaths = ["/bff"], TokenTimeout = TimeSpan.FromSeconds(30),
        }).ShouldNotBeEmpty();

    /// <summary>What <c>ValidateDataAnnotations</c> (attributes and <see cref="IValidatableObject"/>) reports at start-up.</summary>
    private static List<ValidationResult> Errors(SystemAvailabilityOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}

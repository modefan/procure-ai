using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Shouldly;

namespace ProcureAi.SystemTests;

/// <summary>The whole system starts: every resource healthy (or finished cleanly), the gateway serves Swagger and routes to every service.</summary>
[Collection(SystemCollection.Name)]
public sealed class SystemHealthTests(SystemFixture system)
{
    [Fact]
    public async Task Every_resource_is_healthy_and_swagger_lists_ten_definitions()
    {
        system.RequireReady();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var notifications = system.App.ResourceNotifications;
        var resources = system.Model.Resources
            .Where(r => r is ContainerResource or ProjectResource or ExecutableResource)
            // Aspire's hidden helpers that only run on demand (rebuild a project from the dashboard).
            .Where(r => r.GetType().Name != "ProjectRebuilderResource" && !r.Annotations.OfType<ExplicitStartupAnnotation>().Any())
            .ToList();
        resources.Count(r => r.Name.StartsWith("debezium-", StringComparison.Ordinal))
            .ShouldBe(8, "requestsdb, accessdb, budgetsdb, approvalsdb, purchasingdb, invoicingdb, assistantdb, and the one for MongoDB");
        foreach (var expected in new[] { "suppliers-api", "purchasing-api", "invoicing-api", "notifications-api", "assistant-api", "simulators" })
        {
            resources.ShouldContain(r => r.Name == expected);
        }

        // Run-to-completion resources (the <name>-migrate steps, the web app's npm install, the one-shot setup containers) must have finished with exit code 0;
        // everything else must be running and healthy (the services' /health/ready comes from the same ServiceDefaults).
        foreach (var resource in resources)
        {
            var settled = await notifications.WaitForResourceAsync(
                resource.Name,
                e => e.Snapshot.State?.Text is var state
                     && (state == KnownResourceStates.Finished || state == KnownResourceStates.Exited || state == KnownResourceStates.FailedToStart
                         || (state == KnownResourceStates.Running && e.Snapshot.HealthStatus == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy)),
                cts.Token);
            var snapshot = settled.Snapshot;
            if (snapshot.State?.Text == KnownResourceStates.Running)
            {
                continue;
            }

            // A project or executable that ran to completion ends Finished; a one-shot container (postgres-roles, mongo-users) ends Exited.
            snapshot.State?.Text.ShouldBe(resource is ContainerResource ? KnownResourceStates.Exited : KnownResourceStates.Finished, $"{resource.Name} state");
            snapshot.ExitCode.ShouldBe(0, $"{resource.Name} exit code");
        }

        // The gateway's Swagger UI offers one definition per service, and serves each document.
        using var gateway = system.Gateway();
        var config = await gateway.GetStringAsync("/swagger/index.js", TestContext.Current.CancellationToken);
        foreach (var service in SystemFixture.Services)
        {
            config.ShouldContain($"/openapi/{service}/v1.json");
            using var document = await gateway.GetAsync($"/openapi/{service}/v1.json", TestContext.Current.CancellationToken);
            document.StatusCode.ShouldBe(HttpStatusCode.OK, $"{service} document");
            (await document.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"/api/v1/{service}", Case.Sensitive, $"{service} document lists its API");
        }

        System.Text.RegularExpressions.Regex.Matches(config, @"/openapi/\w+/v1\.json").Select(m => m.Value).Distinct().Count().ShouldBe(SystemFixture.Services.Length);
    }

    /// <summary>
    /// The system status screen's endpoint (plan 06 Task 4) lists every service API as Healthy, each with at least one instance: every
    /// service publishes its snapshot to Redis and Notifications merges them. A service without a snapshot would be listed as Down.
    /// </summary>
    [Fact]
    public async Task Status_endpoint_lists_every_service_healthy()
    {
        system.RequireReady();
        using var gateway = system.Gateway();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        string last = "";
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications/system-status");
            request.Headers.Add("X-Api-Key", system.AdminApiKey);
            request.Headers.Add("X-Tenant-Id", SystemFixture.Tenant);
            using var response = await gateway.SendAsync(request, TestContext.Current.CancellationToken);
            last = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, last);

            using var json = System.Text.Json.JsonDocument.Parse(last);
            var services = json.RootElement.GetProperty("services").EnumerateArray()
                .ToDictionary(s => s.GetProperty("name").GetString()!, s => (Health: s.GetProperty("health").GetString(), Instances: s.GetProperty("instances").GetInt32()));
            var expected = SystemFixture.Services.Select(s => $"{s}-api").ToArray();
            if (expected.All(name => services.TryGetValue(name, out var s) && s.Health == "Healthy" && s.Instances >= 1))
            {
                services.Keys.Order().ShouldBe(expected.Order(), "the status lists exactly the service APIs");
                return;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new ShouldAssertException($"Not every service is Healthy on the status endpoint after 2 minutes: {last}");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The supplier catalog through the gateway: proves the route and that Suppliers reaches its MongoDB database with the AppHost's
    /// connection string (it must authenticate against <c>admin</c>, where Aspire creates the user).
    /// </summary>
    [Fact]
    public async Task Gateway_lists_the_suppliers_of_the_tenant_from_mongodb()
    {
        system.RequireReady();
        using var gateway = system.Gateway();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/suppliers");
        request.Headers.Add("X-Api-Key", system.AdminApiKey);
        request.Headers.Add("X-On-Behalf-Of-User", SystemFixture.Tom);
        request.Headers.Add("X-Tenant-Id", SystemFixture.Tenant);

        using var response = await gateway.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldContain("contoso-hardware");
        body.ShouldContain("fabrikam-office");
    }
}

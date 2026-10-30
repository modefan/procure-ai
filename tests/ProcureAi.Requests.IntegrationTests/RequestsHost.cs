using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Application.Access;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.Requests.Application.Abstractions;
using ProcureAi.Requests.Infrastructure;
using ProcureAi.Testing;
using Wolverine;

namespace ProcureAi.Requests.IntegrationTests;

/// <summary>A Wolverine host wired the way the Requests API will be, minus HTTP.</summary>
public sealed class RequestsHost : IAsyncDisposable
{
    private RequestsHost(IHost host, TestCaller caller)
    {
        Host = host;
        Caller = caller;
    }

    public IHost Host { get; }

    public TestCaller Caller { get; }

    /// <summary>The cost centers each user acts for (everyone: <c>CC-100</c> until a test says otherwise).</summary>
    public FakeAccessDirectory Access => Host.Services.GetRequiredService<FakeAccessDirectory>();

    /// <summary>A new DI scope with its own caller, like one HTTP request.</summary>
    public (IServiceScope Scope, ScopedTestCaller Caller, RequestsDbContext Db) NewScope(string userId, string tenantId)
    {
        var scope = Host.Services.CreateScope();
        var caller = (ScopedTestCaller)scope.ServiceProvider.GetRequiredService<ICallerContext>();
        caller.UserId = userId;
        caller.TenantId = tenantId;
        return (scope, caller, scope.ServiceProvider.GetRequiredService<RequestsDbContext>());
    }

    public IMessageBus Bus => Host.MessageBus();

    /// <param name="useRealCaller">Registers the API's own <c>CallerContext</c> (user and tenant from message headers) instead of the settable test caller.</param>
    public static async Task<RequestsHost> StartAsync(
        string postgres, string rabbit, Action<WolverineOptions>? configure = null, bool useRealCaller = false)
    {
        var caller = new TestCaller();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:requestsdb"] = postgres,
            ["ConnectionStrings:rabbitmq"] = rabbit,
            ["Wolverine:AutoBuildMessageStorageOnStartup"] = "CreateOrUpdate",
            ["Requests:ConcurrencyRetry:Attempts"] = "10",
            ["Requests:ConcurrencyRetry:InitialDelay"] = "00:00:00.050",
            ["Requests:ConcurrencyRetry:Multiplier"] = "2.0",
            ["Requests:ConcurrencyRetry:MaxDelay"] = "00:00:02",
            ["Requests:ConcurrencyRetry:JitterFraction"] = "0.5",
        }).Build();
        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddConfiguration(configuration))
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(caller);
                if (useRealCaller)
                {
                    services.AddProcureAiCaller("requests-api");
                }
                else
                {
                    // Scoped, as in the API: a singleton here hid a captive-dependency bug in the audit interceptor.
                    services.AddScoped<ICallerContext, ScopedTestCaller>();
                }

                services.AddRequestsInfrastructure(context.Configuration);
                // No HTTP here, so no Approvals to ask: every submission has an eligible approver.
                services.AddSingleton<IApprovalPreview, AlwaysPossiblePreview>();
                // Nor Suppliers: the default demo catalog (what the stub Suppliers offers) answers for every tenant.
                services.AddSingleton<ISupplierCatalog, DefaultCatalog>();
                // Nor Access: the fake says which cost centers each user acts for.
                services.AddSingleton<FakeAccessDirectory>();
                services.AddSingleton<IAccessDirectory>(sp => sp.GetRequiredService<FakeAccessDirectory>());
                services.AddProcureAiCallerScope();
            })
            .UseDefaultServiceProvider(o =>
            {
                o.ValidateScopes = true;
                o.ValidateOnBuild = true;
            })
            .UseWolverine(options =>
            {
                options.ConfigureRequests(configuration, "requests-api");
                configure?.Invoke(options);
            })
            .Build();
        await host.StartAsync();
        return new RequestsHost(host, caller);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

/// <summary>The approval pre-check of a host without Approvals: always possible.</summary>
public sealed class AlwaysPossiblePreview : IApprovalPreview
{
    public Task<ApprovalPreviewResult> CheckAsync(
        string tenantId, string costCenterCode, decimal totalInclVat, string requesterId, CancellationToken cancellationToken) =>
        Task.FromResult(new ApprovalPreviewResult(true, null));
}

/// <summary>The supplier catalog of a host without Suppliers: the stub's default supplier and items, for every tenant.</summary>
public sealed class DefaultCatalog : ISupplierCatalog
{
    public Task<SupplierItems?> GetItemsAsync(
        string supplierCode, IReadOnlyCollection<string> skus, string tenantId, CancellationToken cancellationToken)
    {
        if (supplierCode != StubSuppliersServer.DefaultSupplier)
        {
            return Task.FromResult<SupplierItems?>(null);
        }

        var items = StubSuppliersServer.DefaultItems
            .Where(i => skus.Contains(i.Sku))
            .ToDictionary(i => i.Sku, i => new CatalogPrice(i.Description, i.UnitPriceExclVat, i.Active));
        return Task.FromResult<SupplierItems?>(new SupplierItems(supplierCode, "Contoso Hardware", true, items));
    }
}

/// <summary>Access for a host without HTTP: only the cost centers of a user are known.</summary>
public sealed class FakeAccessDirectory : IAccessDirectory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Tenant, string UserId), string[]> _costCenters = new();

    public void SetCostCenters(string tenantId, string userId, params string[] codes) => _costCenters[(tenantId, userId)] = codes;

    public ValueTask<EffectiveAccess> GetEffectiveAccessAsync(
        string userId, IReadOnlyCollection<string> roles, string tenantId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EffectiveAccess([], _costCenters.GetValueOrDefault((tenantId, userId)) ?? ["CC-100"], 0m));

    public ValueTask<CostCenterApprovers> GetApproversAsync(string costCenterCode, string tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<string?> GetDisplayNameAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<DirectoryUser?> GetUserAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<AccessExplanation?> ExplainAsync(string userId, string tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

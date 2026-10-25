using ProcureAi.Requests.Api;

var app = RequestsApi.Build(args);

if (args.Contains(RequestsApi.MigrateFlag))
{
    // Run by the deployment before the API starts (Aspire resource / Kubernetes job). Nothing migrates at normal startup.
    await RequestsApi.MigrateAsync(app);
    return;
}

await app.RunAsync();

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;

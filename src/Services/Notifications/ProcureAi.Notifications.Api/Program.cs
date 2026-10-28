using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.Notifications.Api;

var app = NotificationsApi.Build(args);

// No database (Service:Store=None): --migrate has nothing to do, and the AppHost adds no migrate resource.
await ServiceHost.RunAsync(app, args, migrate: null);

/// <summary>Lets the integration tests start the real application through WebApplicationFactory.</summary>
public partial class Program;

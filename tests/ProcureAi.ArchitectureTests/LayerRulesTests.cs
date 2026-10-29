using System.Reflection;
using Assembly = System.Reflection.Assembly;
using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using ProcureAi.BuildingBlocks.Application;
using ProcureAi.BuildingBlocks.Hosting;
using ProcureAi.BuildingBlocks.Messaging;
using ProcureAi.BuildingBlocks.Persistence;
using Shouldly;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace ProcureAi.ArchitectureTests;

/// <summary>
/// The Clean Architecture rules of CLAUDE.md, enforced for every service layer assembly found next to the test
/// (ProcureAi.&lt;Service&gt;.Domain / .Application / .Infrastructure / .Api). Library: ArchUnitNET (TngTech), because it
/// is actively maintained (0.13.x) and can express "depends on a type that carries an attribute"; NetArchTest cannot.
/// </summary>
public sealed class LayerRulesTests
{
    // ProcureAi.Requests.Domain, ProcureAi.BuildingBlocks.Domain, ...
    private static readonly Regex LayerAssembly = new(@"^ProcureAi\.(?<service>\w+)\.(?<layer>Domain|Application|Infrastructure|Api)$");

    private static readonly Assembly[] LayerAssemblies = Directory
        .EnumerateFiles(AppContext.BaseDirectory, "ProcureAi.*.dll")
        .Select(Path.GetFileNameWithoutExtension)
        .Where(name => LayerAssembly.IsMatch(name!))
        .Select(name => Assembly.Load(name!))
        .ToArray();

    // Loaded together with the layers so that dependencies on them resolve to real types, not stubs.
    private static readonly Assembly[] Supporting =
    [
        typeof(IAuditContextSource).Assembly, typeof(ICallerContext).Assembly, typeof(CallerContext).Assembly, typeof(ServiceHost).Assembly,
        typeof(ProcureAi.BuildingBlocks.Web.WebExtensions).Assembly, Assembly.Load("ProcureAi.ServiceDefaults"),
        typeof(ProcureAi.BuildingBlocks.Persistence.Mongo.MongoSetup).Assembly,
    ];

    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies([.. LayerAssemblies, .. Supporting.Except(LayerAssemblies)])
        .Build();

    // DbContexts that deliberately do not carry the audit context (none today). Add the full type name here, with a reason.
    private static readonly HashSet<string> AuditExempt = [];

    private static Assembly[] Layer(string layer)
    {
        var found = LayerAssemblies.Where(a => LayerAssembly.Match(a.GetName().Name!).Groups["layer"].Value == layer).ToArray();
        // A rule over zero assemblies would pass on nothing.
        found.ShouldNotBeEmpty($"no ProcureAi.*.{layer} assembly found");
        return found;
    }

    private static string Name(Assembly a) => a.GetName().Name!;

    [Fact]
    public void Every_service_has_all_four_layers()
    {
        var services = LayerAssemblies.Select(a => LayerAssembly.Match(Name(a)).Groups["service"].Value)
            .Where(s => s != "BuildingBlocks").Distinct().ToList();
        services.ShouldNotBeEmpty();
        foreach (var service in services)
        {
            foreach (var layer in new[] { "Domain", "Application", "Infrastructure", "Api" })
            {
                LayerAssemblies.Select(Name).ShouldContain($"ProcureAi.{service}.{layer}");
            }
        }
    }

    [Fact]
    public void Domain_has_no_dependency_on_infrastructure_or_aspnet()
    {
        var domain = Layer("Domain");
        Types().That().ResideInAssembly(domain[0], domain[1..])
            .Should().NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching(@"^ProcureAi\.\w+\.(Application|Infrastructure|Api)(\..*)?$")
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(
                @"^(Microsoft\.AspNetCore|Microsoft\.EntityFrameworkCore|Microsoft\.Extensions\.(DependencyInjection|Hosting)|Npgsql|Wolverine|JasperFx|StackExchange|ZiggyCreatures|MongoDB|Marten|Weasel)(\..*)?$")
            .Check(Architecture);
    }

    [Fact]
    public void Application_does_not_reference_Infrastructure_or_Api()
    {
        // Same bans as the Domain rule except Wolverine and its support library JasperFx (handlers and messages live here). Allowed on purpose:
        // Microsoft.EntityFrameworkCore core types (IQueryable/DbSet/async LINQ behind IRequestsDbContext, the
        // read-side abstraction; providers such as Npgsql and any concrete DbContext stay in Infrastructure),
        // Riok.Mapperly attributes, Microsoft.Extensions abstractions (Options, Logging) and the Domain and Contracts.
        // Document stores stay in Infrastructure too: Audit's Application sees IAuditStore, never Marten (or its Weasel layer).
        var application = Layer("Application");
        Types().That().ResideInAssembly(application[0], application[1..])
            .Should().NotDependOnAnyTypesThat()
            .ResideInNamespaceMatching(@"^ProcureAi\.\w+\.(Infrastructure|Api)(\..*)?$")
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(
                @"^(Microsoft\.AspNetCore|Npgsql|StackExchange|ZiggyCreatures|MongoDB|Marten|Weasel)(\..*)?$")
            // The caller reaches Application as ICallerContext (BuildingBlocks.Application); the web, messaging and persistence
            // building blocks are for the outer layers.
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(
                @"^ProcureAi\.BuildingBlocks\.(Web|Messaging|Persistence|Hosting)(\..*)?$")
            .Check(Architecture);
    }

    [Fact]
    public void MongoDB_building_block_is_used_only_by_Infrastructure_and_Hosting()
    {
        // The MongoDB services' data access (TenantCollection, migrations) belongs in Infrastructure; the composition root (Hosting)
        // wires its readiness and migrate run. Domain, Application and the Api never see it (nor the MongoDB driver, see the other rules).
        foreach (var layer in new[] { "Domain", "Application", "Api" })
        {
            var assemblies = Layer(layer);
            Types().That().ResideInAssembly(assemblies[0], assemblies[1..])
                .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^ProcureAi\.BuildingBlocks\.Persistence\.Mongo(\..*)?$")
                .Check(Architecture);
        }
    }

    [Fact]
    public void Only_Api_projects_use_the_Hosting_building_block()
    {
        // ServiceHost is the composition root: it wires web, messaging and persistence together, which is the Api's job.
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var assemblies = Layer(layer);
            Types().That().ResideInAssembly(assemblies[0], assemblies[1..])
                .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^ProcureAi\.BuildingBlocks\.Hosting(\..*)?$")
                .Check(Architecture);
        }
    }

    /// <summary>Namespaces any service may use: the shared libraries. Everything else under ProcureAi.* is a different component.</summary>
    private static readonly string[] SharedRoots = ["BuildingBlocks", "Contracts", "ServiceDefaults"];

    [Fact]
    public void A_service_depends_only_on_itself_and_the_shared_libraries()
    {
        // Services talk through events (Contracts) and HTTP, never by referencing each other's code, so each can be
        // built, tested and deployed alone. The Gateway and AppHost are not services, so a service may not use them either.
        var services = LayerAssemblies.Select(a => LayerAssembly.Match(Name(a)).Groups["service"].Value)
            .Where(s => !SharedRoots.Contains(s)).Distinct().ToList();
        services.ShouldNotBeEmpty();
        foreach (var service in services)
        {
            var own = LayerAssemblies.Where(a => LayerAssembly.Match(Name(a)).Groups["service"].Value == service).ToArray();
            var allowed = string.Join('|', SharedRoots.Append(Regex.Escape(service)));
            Types().That().ResideInAssembly(own[0], own[1..])
                .Should().NotDependOnAnyTypesThat()
                .ResideInNamespaceMatching($@"^ProcureAi\.(?!({allowed})(\.|$))\w+(\..*)?$")
                .Check(Architecture);
        }
    }

    [Fact]
    public void Simulators_depend_only_on_ServiceDefaults()
    {
        // The simulators play another company's systems (a supplier, an ERP). Sharing BuildingBlocks or Contracts with the services
        // would let a contract change hide on both sides; they talk through HTTP and signed webhooks only, with their own signing code.
        var simulators = Assembly.Load("ProcureAi.Simulators");
        var ours = simulators.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("ProcureAi.", StringComparison.Ordinal)).ToList();
        ours.ShouldBeSubsetOf(["ProcureAi.ServiceDefaults"], "the simulators may reference only ServiceDefaults");

        var project = Path.Combine(FindRepositoryRoot(), "src", "Simulators", "ProcureAi.Simulators", "ProcureAi.Simulators.csproj");
        var references = File.ReadAllLines(project).Where(l => l.Contains("ProjectReference", StringComparison.Ordinal)).ToList();
        references.ShouldNotBeEmpty();
        references.ShouldAllBe(l => l.Contains("ProcureAi.ServiceDefaults.csproj", StringComparison.Ordinal), "project references of the simulators");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProcureAi.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("ProcureAi.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Controllers_do_not_use_DbContext()
    {
        var api = Layer("Api");
        Classes().That().ResideInAssembly(api[0], api[1..])
            .And().HaveNameEndingWith("Controller")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^(Microsoft\.EntityFrameworkCore|Npgsql)(\..*)?$")
            .AndShould().NotDependOnAnyTypesThat().HaveNameMatching(@".*DbContext$")
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^ProcureAi\.\w+\.Infrastructure(\..*)?$")
            .Check(Architecture);
    }

    [Fact]
    public void Generated_code_is_not_referenced_by_domain()
    {
        // A guard: today the domain has no way to reach generated code, so this stays green until someone adds it.
        // .NET side: code produced by tools at build or run time (Wolverine's pre-built handlers in Internal.Generated.*,
        // Mapperly mappers, anything marked [GeneratedCode]) may depend on the domain, never the other way round.
        // The domain stays hand-written and free of tool output. (OpenAPI documents are JSON, not code; the Angular
        // client rules belong to the web app.)
        var domain = Layer("Domain");
        Types().That().ResideInAssembly(domain[0], domain[1..])
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Internal\.Generated(\..*)?$")
            .AndShould().NotDependOnAnyTypesThat().HaveAnyAttributes(typeof(System.CodeDom.Compiler.GeneratedCodeAttribute))
            .Check(Architecture);
    }

    private static bool DerivesFromDbContext(Class type)
    {
        for (var baseClass = type.BaseClass; baseClass is not null; baseClass = baseClass.BaseClass)
        {
            if (baseClass.FullName == "Microsoft.EntityFrameworkCore.DbContext")
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void Every_DbContext_provides_the_audit_context()
    {
        // AuditSaveChangesInterceptor reads the caller through IAuditContextSource; a DbContext without it would
        // silently record every change as "system".
        var infrastructure = Layer("Infrastructure");
        Classes().That().ResideInAssembly(infrastructure[0], infrastructure[1..])
            .And().FollowCustomPredicate(DerivesFromDbContext, "derive from DbContext")
            .And().FollowCustomPredicate(t => !AuditExempt.Contains(t.FullName), "are not exempt from the audit context")
            .Should().ImplementInterface(typeof(IAuditContextSource))
            .Check(Architecture);
    }

    /// <summary>The model provider SDKs: only the Assistant's DI/config setup may name them (plan 05 Global Constraints).</summary>
    private static readonly string[] ProviderSdks = ["OpenAI", "Microsoft.Extensions.AI.OpenAI"];

    [Fact]
    public void Only_assistant_infrastructure_references_provider_sdks()
    {
        // Application code sees IChatClient, IEmbeddingGenerator and AIFunction (Microsoft.Extensions.AI); the provider (OpenAI, Azure
        // Foundry's OpenAI v1 endpoint) is chosen by configuration in ProcureAi.Assistant.Infrastructure (AiSetup). Checked on the compiled
        // references: an assembly references another only when its code uses a type of it.
        const string allowed = "ProcureAi.Assistant.Infrastructure";
        var ours = Directory.EnumerateFiles(AppContext.BaseDirectory, "ProcureAi.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith("Tests", StringComparison.Ordinal))
            .Select(name => Assembly.Load(name!))
            .ToList();
        ours.Select(Name).ShouldContain(allowed);

        foreach (var assembly in ours.Where(a => Name(a) != allowed))
        {
            assembly.GetReferencedAssemblies().Select(r => r.Name).ShouldNotContain(
                r => ProviderSdks.Contains(r), $"{Name(assembly)} references a provider SDK; only {allowed} may");
        }

        // The rule only means something while the provider really is wired there.
        ours.Single(a => Name(a) == allowed).GetReferencedAssemblies().Select(r => r.Name).ShouldContain("Microsoft.Extensions.AI.OpenAI");
    }

    [Fact]
    public void No_static_Serilog_Log_in_src()
    {
        // The hosts keep their own logger (preserveStaticLogger: true) and never touch the process-wide Serilog.Log.Logger.
        // A static Log.* call would write to a logger nobody configured and be silent, so it is banned in our assemblies.
        var ours = LayerAssemblies.Concat(Supporting).Distinct().ToArray();
        Types().That().ResideInAssembly(ours[0], ours[1..])
            .Should().NotDependOnAnyTypesThat().HaveFullName("Serilog.Log")
            .Check(Architecture);
    }
}

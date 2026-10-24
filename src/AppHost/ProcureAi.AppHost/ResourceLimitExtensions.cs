namespace ProcureAi.AppHost;

public static class ResourceLimitExtensions
{
    /// <summary>Applies the container memory and CPU caps (docker run --memory / --cpus).</summary>
    public static IResourceBuilder<T> WithContainerLimits<T>(this IResourceBuilder<T> builder, ContainerLimits limits)
        where T : ContainerResource =>
        builder.WithContainerRuntimeArgs("--memory", $"{limits.MemoryMb}m", "--cpus", limits.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Caps the GC of a .NET project resource. Later tasks call this for every service project.</summary>
    public static IResourceBuilder<T> WithDotNetLimits<T>(this IResourceBuilder<T> builder, DotNetLimits limits)
        where T : ProjectResource =>
        builder
            .WithEnvironment("DOTNET_gcServer", limits.GcServer.ToString())
            .WithEnvironment("DOTNET_GCConserveMemory", limits.GcConserveMemory.ToString())
            .WithEnvironment("DOTNET_GCHeapHardLimit", limits.GcHeapHardLimit);

    /// <summary>Caps the Node heap of the Angular dev server. Used when the frontend is added.</summary>
    public static IResourceBuilder<T> WithNodeLimits<T>(this IResourceBuilder<T> builder, AngularLimits limits)
        where T : IResourceWithEnvironment =>
        builder.WithEnvironment("NODE_OPTIONS", limits.NodeOptions);
}

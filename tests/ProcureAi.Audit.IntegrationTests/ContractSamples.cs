using System.Reflection;

namespace ProcureAi.Audit.IntegrationTests;

/// <summary>Sample instances of any contract by reflection, so a new contract needs no test changes (a new parameter type fails loudly).</summary>
internal static class ContractSamples
{
    /// <summary>An instance of a contract: the tenant and request of the test, plausible values elsewhere.</summary>
    public static object Of(Type type, string tenant, Guid requestId, string userId)
    {
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select(p => Value(p, p.ParameterType, tenant, requestId, userId)).ToArray();
        return constructor.Invoke(arguments);
    }

    private static object? Value(ParameterInfo parameter, Type type, string tenant, Guid requestId, string userId)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return parameter switch
        {
            { Name: "TenantId" } => tenant,
            { Name: "RequestId" } => requestId,
            _ when type == typeof(string) => $"{parameter.Name}-sample",
            _ when type == typeof(Guid) => Guid.NewGuid(),
            _ when type == typeof(decimal) => 1_000m,
            _ when type == typeof(int) => 1,
            _ when type == typeof(DateTimeOffset) => DateTimeOffset.UtcNow,
            _ when type == typeof(DateOnly) => DateOnly.FromDateTime(DateTime.UtcNow),
            _ when type == typeof(IReadOnlyList<string>) => new[] { userId },
            _ when type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) => List(type.GetGenericArguments()[0], tenant, requestId, userId),
            _ => throw new InvalidOperationException($"No sample value for {parameter.Member.DeclaringType!.Name}.{parameter.Name} ({parameter.ParameterType})."),
        };
    }

    private static object List(Type element, string tenant, Guid requestId, string userId)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        list.Add(Of(element, tenant, requestId, userId));
        return list;
    }
}

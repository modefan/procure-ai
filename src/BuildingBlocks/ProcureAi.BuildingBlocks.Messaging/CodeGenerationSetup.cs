using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace ProcureAi.BuildingBlocks.Messaging;

/// <summary>
/// How Wolverine (handlers) and Marten (Audit's document storage) get their generated code, from configuration:
/// <list type="bullet">
/// <item><c>Wolverine:TypeLoadMode</c>: <c>Dynamic</c> (the default: Roslyn compiles the code at startup and on the first message;
/// AppHost and tests), <c>Auto</c> (the pre-generated types when they exist, else compile), or <c>Static</c> (only the pre-generated
/// types committed under the API's <c>Internal/Generated</c>; a missing one fails at startup). The container images run <c>Static</c>
/// (src/Directory.Build.props): Roslyn then never loads, which saves the 300-600 MiB of native memory its compilations kept per API
/// (plan 07 Task 5b measurements).</item>
/// <item><c>Wolverine:GeneratedCodeOutputPath</c>: where <c>codegen write</c> writes the code (default: the content root's
/// <c>Internal/Generated</c>). The <c>WriteGeneratedCode</c> build target sets it (docs/generated-code.md).</item>
/// </list>
/// Set on the JasperFx defaults, which Wolverine and Marten both read, for the Development and Production profiles alike.
/// </summary>
public static class CodeGenerationSetup
{
    public const string TypeLoadModeSetting = "Wolverine:TypeLoadMode";
    public const string GeneratedCodeOutputPathSetting = "Wolverine:GeneratedCodeOutputPath";

    public static IServiceCollection AddProcureAiCodeGeneration(this IServiceCollection services, IConfiguration configuration)
    {
        var mode = TypeLoadModeFrom(configuration);
        var outputPath = configuration[GeneratedCodeOutputPathSetting];
        services.CritterStackDefaults(defaults =>
        {
            foreach (var profile in new[] { defaults.Development, defaults.Production })
            {
                profile.GeneratedCodeMode = mode;
                // Static: every type must be there when the host starts, not only when its first message arrives.
                profile.AssertAllPreGeneratedTypesExist = mode == TypeLoadMode.Static;
            }

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                defaults.GeneratedCodeOutputPath = outputPath;
                defaults.Development.GeneratedCodeOutputPath = outputPath;
                defaults.Production.GeneratedCodeOutputPath = outputPath;
            }
        });
        return services;
    }

    /// <summary>
    /// Points Wolverine at the assembly that holds the pre-generated types: the service API (the entry assembly), where
    /// <c>Internal/Generated</c> is compiled. Left alone in <c>Dynamic</c> mode (nothing is loaded from it; tests keep their defaults).
    /// Without it Wolverine guesses the assembly that called <c>UseWolverine</c> setup code (BuildingBlocks.Messaging) and, in
    /// <c>Static</c> mode, finds none of the types.
    /// </summary>
    public static WolverineOptions UsePreGeneratedCodeOfTheApi(this WolverineOptions options, IConfiguration configuration)
    {
        if (TypeLoadModeFrom(configuration) != TypeLoadMode.Dynamic && Assembly.GetEntryAssembly() is { } api)
        {
            options.ApplicationAssembly = api;
        }

        return options;
    }

    /// <summary>The configured mode; <c>Dynamic</c> when unset. An unknown value fails at startup.</summary>
    public static TypeLoadMode TypeLoadModeFrom(IConfiguration configuration)
    {
        var value = configuration[TypeLoadModeSetting];
        if (string.IsNullOrWhiteSpace(value))
        {
            return TypeLoadMode.Dynamic;
        }

        return Enum.TryParse<TypeLoadMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException(
                $"'{TypeLoadModeSetting}' is '{value}'; expected one of {string.Join(", ", Enum.GetNames<TypeLoadMode>())}.");
    }
}

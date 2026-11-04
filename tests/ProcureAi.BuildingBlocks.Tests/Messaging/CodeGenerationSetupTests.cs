using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Messaging;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Messaging;

public sealed class CodeGenerationSetupTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Unset_means_dynamic_as_before()
    {
        CodeGenerationSetup.TypeLoadModeFrom(Config()).ShouldBe(TypeLoadMode.Dynamic);
    }

    [Theory]
    [InlineData("Static", TypeLoadMode.Static)]
    [InlineData("auto", TypeLoadMode.Auto)]
    [InlineData("Dynamic", TypeLoadMode.Dynamic)]
    public void The_mode_comes_from_configuration(string value, TypeLoadMode expected)
    {
        CodeGenerationSetup.TypeLoadModeFrom(Config(("Wolverine:TypeLoadMode", value))).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Precompiled")]
    [InlineData("7")]
    public void An_unknown_mode_fails(string value)
    {
        Should.Throw<InvalidOperationException>(() => CodeGenerationSetup.TypeLoadModeFrom(Config(("Wolverine:TypeLoadMode", value))))
            .Message.ShouldContain("Wolverine:TypeLoadMode");
    }

    [Fact]
    public void Static_loads_the_types_from_the_entry_assembly_where_the_api_compiles_them()
    {
        var options = new Wolverine.WolverineOptions();
        options.UsePreGeneratedCodeOfTheApi(Config(("Wolverine:TypeLoadMode", "Static")));
        options.ApplicationAssembly.ShouldBe(System.Reflection.Assembly.GetEntryAssembly());
    }

    [Fact]
    public void Dynamic_leaves_the_application_assembly_alone()
    {
        var options = new Wolverine.WolverineOptions();
        var before = options.ApplicationAssembly;
        options.UsePreGeneratedCodeOfTheApi(Config());
        options.ApplicationAssembly.ShouldBe(before);
    }

    [Fact]
    public void Static_applies_to_both_profiles_and_asserts_every_type_exists()
    {
        var services = new ServiceCollection();
        services.AddProcureAiCodeGeneration(Config(("Wolverine:TypeLoadMode", "Static"), ("Wolverine:GeneratedCodeOutputPath", "/tmp/gen")));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<JasperFxOptions>();

        foreach (var profile in new[] { options.Development, options.Production })
        {
            profile.GeneratedCodeMode.ShouldBe(TypeLoadMode.Static);
            profile.AssertAllPreGeneratedTypesExist.ShouldBeTrue();
        }

        options.GeneratedCodeOutputPath.ShouldBe("/tmp/gen");
    }
}

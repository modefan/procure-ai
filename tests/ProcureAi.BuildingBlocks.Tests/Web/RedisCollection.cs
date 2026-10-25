using ProcureAi.Testing;

namespace ProcureAi.BuildingBlocks.Tests.Web;

/// <summary>One Redis container shared by the caching tests.</summary>
[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}

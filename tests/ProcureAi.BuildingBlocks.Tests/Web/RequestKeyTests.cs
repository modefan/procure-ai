using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

public sealed class RequestKeyTests
{
    private sealed class FakeUser(string userId, string? tenant) : ICurrentUser
    {
        public string UserId => userId;
        public string? TenantId => tenant;
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
        public bool IsAdminApiKey => false;
        public string? CallingService => null;
        public string? OnBehalfOfUser => null;
    }

    private static DefaultHttpContext Context(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    [Fact]
    public void Read_key_ignores_query_order_and_path_case()
    {
        var user = new FakeUser("u1", "acme");

        RequestKey.ForRead(Context("/api/Items", "?a=1&b=2"), user)
            .ShouldBe(RequestKey.ForRead(Context("/api/items", "?b=2&a=1"), user));
    }

    [Fact]
    public void Read_key_differs_by_tenant_and_user_and_query_value()
    {
        var context = Context("/api/items", "?a=1");
        var baseKey = RequestKey.ForRead(context, new FakeUser("u1", "acme"));

        RequestKey.ForRead(context, new FakeUser("u1", "globex")).ShouldNotBe(baseKey);
        RequestKey.ForRead(context, new FakeUser("u2", "acme")).ShouldNotBe(baseKey);
        RequestKey.ForRead(context, new FakeUser("u1", null)).ShouldNotBe(baseKey);
        RequestKey.ForRead(Context("/api/items", "?a=2"), new FakeUser("u1", "acme")).ShouldNotBe(baseKey);
    }

    [Fact]
    public void Write_key_cannot_be_forged_by_moving_text_between_parts()
    {
        var context = Context("/", string.Empty);

        RequestKey.ForWrite(context, new FakeUser("a", "b"), "c|d")
            .ShouldNotBe(RequestKey.ForWrite(context, new FakeUser("a", "b|c"), "d"));
    }

    [Fact]
    public void Caching_setup_without_a_redis_connection_string_fails_at_startup()
    {
        var builder = WebApplication.CreateBuilder();

        Should.Throw<InvalidOperationException>(() => builder.AddProcureAiCaching());
    }
}

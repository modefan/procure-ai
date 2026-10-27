using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProcureAi.BuildingBlocks.Web.Auth;
using ProcureAi.BuildingBlocks.Web.Caching;
using Shouldly;

namespace ProcureAi.BuildingBlocks.Tests.Web;

public sealed class RequestKeyTests
{
    private sealed class FakeUser(string userId, string? tenant, string? onBehalfOf = null) : ICurrentUser
    {
        public string UserId => userId;
        public string? TenantId => tenant;
        public IReadOnlySet<string> Roles { get; } = new HashSet<string>();
        public bool IsAuthenticated => true;
        public bool IsAdminApiKey => false;
        public bool IsServiceAccount => false;
        public string? CallingService => null;
        public string? OnBehalfOfUser => onBehalfOf;
    }

    private static DefaultHttpContext Context(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    [Fact]
    public void Read_key_ignores_query_order()
    {
        var user = new FakeUser("u1", "acme");

        RequestKey.ForRead(Context("/api/items", "?a=1&b=2"), user)
            .ShouldBe(RequestKey.ForRead(Context("/api/items", "?b=2&a=1"), user));
    }

    [Fact]
    public void Read_key_keeps_path_case_because_route_ids_can_be_case_sensitive()
    {
        var user = new FakeUser("u1", "acme");

        RequestKey.ForRead(Context("/suppliers/AbC", string.Empty), user)
            .ShouldNotBe(RequestKey.ForRead(Context("/suppliers/abc", string.Empty), user));
    }

    [Fact]
    public void Read_key_cannot_be_forged_with_encoded_query_separators()
    {
        var user = new FakeUser("u1", "acme");

        RequestKey.ForRead(Context("/api/items", "?a=1%26b%3D2"), user)
            .ShouldNotBe(RequestKey.ForRead(Context("/api/items", "?a=1&b=2"), user));
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
    public void Keys_differ_by_the_user_the_admin_key_or_a_service_acts_for()
    {
        // The admin API key (and a service) answers for X-On-Behalf-Of-User: an inbox read for maria must never be served to dept.
        var context = Context("/api/v1/approvals/inbox", string.Empty);
        var forMaria = new FakeUser("claude-admin", "acme", "maria");
        var forDept = new FakeUser("claude-admin", "acme", "dept");
        var forNobody = new FakeUser("claude-admin", "acme");

        RequestKey.ForRead(context, forMaria).ShouldNotBe(RequestKey.ForRead(context, forDept));
        RequestKey.ForRead(context, forMaria).ShouldNotBe(RequestKey.ForRead(context, forNobody));
        RequestKey.ForWrite(context, forMaria, "k1").ShouldNotBe(RequestKey.ForWrite(context, forDept, "k1"));
        RequestKey.ForWrite(context, forMaria, "k1").ShouldNotBe(RequestKey.ForWrite(context, forNobody, "k1"));
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

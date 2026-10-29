using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProcureAi.Assistant.Application;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.Assistant.Infrastructure.Chat;
using Shouldly;
using StackExchange.Redis;
using static ProcureAi.Assistant.IntegrationTests.ChatApi;

namespace ProcureAi.Assistant.IntegrationTests;

/// <summary>The daily question quota (ruling 7): per tenant and user, per South African calendar day, counted in Redis.</summary>
[Collection(AssistantCollection.Name)]
public sealed class QuotaTests(AssistantApp app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Quota_51st_question_is_429()
    {
        var tenant = AssistantApp.NewTenant();
        await SeedPolicyAsync(app, tenant, StreamingTests.ThresholdRule);
        var token = User(tenant);
        var conversation = await CreateAsync(app.Client, token);
        // 49 questions asked earlier today (counted as the service counts them).
        var quota = app.Services.GetRequiredService<IQuestionQuota>();
        for (var i = 0; i < 49; i++)
        {
            (await quota.TryCountAsync(tenant, "rita", Ct)).Allowed.ShouldBeTrue();
        }

        await AskOkAsync(app.Client, token, conversation, "Do purchases above R 100 000 need finance approval?");
        var used = await GetOkAsync(app.Client, Quota, token);
        used.GetProperty("used").GetInt32().ShouldBe(50);
        used.GetProperty("limit").GetInt32().ShouldBe(50);

        var refused = await AskAsync(app.Client, token, conversation, "And for laptops?");

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests);
        refused.Body.ShouldContain("You've used all 50 questions for today.");
        Messages(await ConversationAsync(app.Client, token, conversation)).Length.ShouldBe(2);
        (await GetOkAsync(app.Client, Quota, token)).GetProperty("used").GetInt32().ShouldBe(50);
        // Another user of the tenant has a quota of their own.
        (await GetOkAsync(app.Client, Quota, User(tenant, "sam"))).GetProperty("used").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Quota_resets_on_next_sast_day()
    {
        var tenant = AssistantApp.NewTenant();
        var options = Options.Create(new AssistantOptions { Quota = new QuotaOptions { QuestionsPerDay = 2 } });
        // 23:59 in Johannesburg (UTC+2) is 21:59 UTC: still the same South African day.
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 29, 21, 59, 0, TimeSpan.Zero));
        var quota = new RedisQuestionQuota(app.Services.GetRequiredService<IConnectionMultiplexer>(), options, clock);

        (await quota.TryCountAsync(tenant, "rita", Ct)).Allowed.ShouldBeTrue();
        (await quota.TryCountAsync(tenant, "rita", Ct)).Used.ShouldBe(2);
        var full = await quota.TryCountAsync(tenant, "rita", Ct);
        full.Allowed.ShouldBeFalse();
        full.Used.ShouldBe(2);
        full.ResetsAt.ShouldBe(new DateTimeOffset(2026, 10, 29, 22, 0, 0, TimeSpan.Zero));

        // 00:00:30 in Johannesburg: a new day (still 29 October in UTC).
        clock.Now = new DateTimeOffset(2026, 10, 29, 22, 0, 30, TimeSpan.Zero);

        var next = await quota.TryCountAsync(tenant, "rita", Ct);
        next.Allowed.ShouldBeTrue();
        next.Used.ShouldBe(1);
        (await quota.GetAsync(tenant, "rita", Ct)).Used.ShouldBe(1);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

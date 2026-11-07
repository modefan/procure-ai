using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ProcureAi.Assistant.Application.Ai;
using ProcureAi.Assistant.Application.Chat;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Assistant.Api.Controllers;

/// <summary>
/// Every request's model calls count against the caller's tenant (<see cref="AiUsageScope"/>): set around the whole action and its
/// result, so a streamed answer is counted too. A global resource filter, so it runs after authentication and before every action.
/// </summary>
public sealed class AiUsageTenantFilter(ICallerContext caller) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (string.IsNullOrWhiteSpace(caller.TenantId))
        {
            await next();
            return;
        }

        using var usage = AiUsageScope.For(caller.TenantId);
        await next();
    }
}

/// <summary>
/// Refuses the action when the caller's tenant has used its daily tokens (<c>AI:DailyTokenCapPerTenant</c>): 429 with
/// <c>Retry-After</c> until midnight and the same title and detail as a refused question (<see cref="AnswerStreamer.TokenCapTitle"/>).
/// 429 like the question quota, because it is a quota that passes: the same request succeeds tomorrow (a 422 would say the request
/// itself is wrong). On the uploads, replacements and scans, which start model work. Runs before the idempotency filter, so a refusal is
/// never stored as the key's result.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TokenCapGateAttribute : Attribute, IFilterFactory, IOrderedFilter
{
    /// <summary>After the upload size limit, before the idempotency filter (order 0).</summary>
    public int Order => int.MinValue + 200;

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new Filter(
            serviceProvider.GetRequiredService<ITenantTokenBudget>(), serviceProvider.GetRequiredService<ICallerContext>(),
            serviceProvider.GetRequiredService<TimeProvider>(), Order);

    private sealed class Filter(ITenantTokenBudget budget, ICallerContext caller, TimeProvider time, int order) : IAsyncResourceFilter, IOrderedFilter
    {
        public int Order => order;

        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            if (!string.IsNullOrWhiteSpace(caller.TenantId)
                && await budget.GetAsync(caller.TenantId, context.HttpContext.RequestAborted) is { Spent: true } spent)
            {
                var wait = spent.ResetsAt - time.GetUtcNow();
                context.HttpContext.Response.Headers.RetryAfter =
                    ((long)Math.Ceiling(Math.Max(0, wait.TotalSeconds))).ToString(CultureInfo.InvariantCulture);
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = AnswerStreamer.TokenCapTitle,
                    Detail = AnswerStreamer.TokenCapDetail,
                })
                {
                    StatusCode = StatusCodes.Status429TooManyRequests,
                    ContentTypes = { "application/problem+json" },
                };
                return;
            }

            await next();
        }
    }
}

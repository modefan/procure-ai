using System.Security.Cryptography;
using System.Text;
using Medallion.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using ProcureAi.BuildingBlocks.Web.Auth;
using ZiggyCreatures.Caching.Fusion;

namespace ProcureAi.BuildingBlocks.Web.Caching;

/// <summary>
/// Resource filter (after authorization). Per tenant + user + <c>Idempotency-Key</c>: the first call takes a
/// Redis lock and runs; duplicates wait for the lock and then get the stored response. The request body hash
/// guards against reusing a key for a different request. 5xx results are never stored; duplicates that were
/// waiting on a failed call get the same error, a later retry runs again.
/// </summary>
public sealed class IdempotencyFilter(
    IFusionCache cache,
    IDistributedLockProvider locks,
    ICurrentUser user,
    IOptions<IdempotencyOptions> options) : IAsyncResourceFilter
{
    private const int MaxKeyLength = 255;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var settings = options.Value;

        var header = http.Request.Headers[IdempotentAttribute.HeaderName];
        if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0]) || header[0]!.Length > MaxKeyLength)
        {
            context.Result = ResponseCapture.Problem(
                StatusCodes.Status400BadRequest, $"A single {IdempotentAttribute.HeaderName} header is required.");
            return;
        }

        var requestHash = await HashRequestAsync(http);
        var key = RequestKey.ForWrite(http, user, header[0]!);
        var storedKey = "idem:" + key;
        var failureKey = "idem-fail:" + key;

        if (await FindAsync(storedKey, requestHash, http) is { } early)
        {
            context.Result = early;
            return;
        }

        var token = http.RequestAborted;
        var waited = false;
        var handle = await locks.TryAcquireLockAsync("lock:" + key, TimeSpan.Zero, token);
        if (handle is null)
        {
            waited = true;
            handle = await locks.TryAcquireLockAsync("lock:" + key, settings.WaitTimeout, token);
            if (handle is null)
            {
                context.Result = ResponseCapture.Busy(http, settings.RetryAfterSeconds);
                return;
            }
        }

        await using (handle)
        {
            // The first call may have finished while we waited.
            if (await FindAsync(storedKey, requestHash, http) is { } stored)
            {
                context.Result = stored;
                return;
            }

            if (waited && await FindAsync(failureKey, requestHash, http) is { } failure)
            {
                context.Result = failure;
                return;
            }

            await cache.RemoveAsync(failureKey, token: token);
            var captured = await ResponseCapture.RunAsync(context, next, requestHash);
            if (captured.StatusCode >= StatusCodes.Status500InternalServerError)
            {
                await cache.SetAsync(failureKey, captured, o => o.SetDuration(settings.FailureShare), token);
            }
            else
            {
                await cache.SetAsync(storedKey, captured, o => o.SetDuration(settings.Retention), token);
            }
        }
    }

    /// <summary>The replay (or 422 when the key was used for a different request) if something is stored.</summary>
    private async Task<IActionResult?> FindAsync(string cacheKey, string requestHash, HttpContext http)
    {
        var found = await cache.TryGetAsync<StoredResponse>(cacheKey, token: http.RequestAborted);
        if (!found.HasValue)
        {
            return null;
        }

        return found.Value.RequestHash == requestHash
            ? found.Value.ToResult()
            : ResponseCapture.Problem(
                StatusCodes.Status422UnprocessableEntity,
                $"This {IdempotentAttribute.HeaderName} was already used with a different request.");
    }

    private static async Task<string> HashRequestAsync(HttpContext http)
    {
        http.Request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{http.Request.Method}\n{http.Request.Path.Value}\n"));
        var buffer = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            sha.AppendData(buffer.AsSpan(0, read));
        }

        http.Request.Body.Position = 0;
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

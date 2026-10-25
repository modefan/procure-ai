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
/// Redis lock and runs; duplicates wait for the lock and then get the stored response. The hash of method, path,
/// query and body guards against reusing a key for a different request. 5xx results are never stored;
/// duplicates that were waiting on a failed call get the same error, a later retry runs again. 409, 412 and 428
/// (conflicts and missing preconditions the client resolves and retries with the same key) are not stored either. The result is stored
/// before it is sent, so a client that disconnects can still retry and get it.
/// A replayed response carries the status, body, Content-Type, Location and ETag only; other headers are not kept.
/// </summary>
public sealed class IdempotencyFilter(
    IFusionCache cache,
    IDistributedLockProvider locks,
    ICurrentUser user,
    IOptions<IdempotencyOptions> options,
    IOptions<FusionCacheSettings> cacheSettings) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var settings = options.Value;
        var padding = cacheSettings.Value.DistributedTtlPadding;

        var header = http.Request.Headers[IdempotentAttribute.HeaderName];
        if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0]) || header[0]!.Length > settings.MaxKeyLength)
        {
            context.Result = ResponseCapture.Problem(
                StatusCodes.Status400BadRequest, $"A single {IdempotentAttribute.HeaderName} header is required.");
            return;
        }

        var requestHash = await HashRequestAsync(http);
        var key = RequestKey.ForWrite(http, user, header[0]!);
        var storedKey = "idem:" + key;
        var failureKey = "idem-fail:" + key;
        var lockName = cacheSettings.Value.KeyPrefix + "lock:" + key;

        if (await FindAsync(storedKey, requestHash, http) is { } early)
        {
            context.Result = early;
            return;
        }

        var (handle, waited) = await LockHelper.AcquireAsync(locks, lockName, settings.WaitTimeout, http.RequestAborted);
        if (handle is null)
        {
            context.Result = ResponseCapture.Busy(http, settings.RetryAfterSeconds);
            return;
        }

        ResponseCapture.Captured captured;
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

            await cache.RemoveAsync(failureKey, token: http.RequestAborted);
            captured = await ResponseCapture.RunAsync(context, next, requestHash);

            // Store before sending, and not cancellable by the client: a disconnect must not lose the result.
            var status = captured.Response.StatusCode;
            if (status >= StatusCodes.Status500InternalServerError)
            {
                await cache.SetAsync(
                    failureKey, captured.Response, o => CacheEntry.ForSharedRecord(o, settings.FailureShare, padding),
                    CancellationToken.None);
            }
            else if (status is not (StatusCodes.Status409Conflict or StatusCodes.Status412PreconditionFailed or StatusCodes.Status428PreconditionRequired))
            {
                await cache.SetAsync(
                    storedKey, captured.Response, o => CacheEntry.ForSharedRecord(o, settings.Retention, padding),
                    CancellationToken.None);
            }
        }

        if (!captured.Threw)
        {
            await ResponseCapture.SendAsync(http, captured.Response);
        }
    }

    /// <summary>The replay (or 422 when the key was used for a different request) if something is stored.</summary>
    private async Task<IActionResult?> FindAsync(string cacheKey, string requestHash, HttpContext http)
    {
        var found = await cache.TryGetAsync<StoredResponse>(cacheKey, CacheEntry.ReadSharedRecord, token: http.RequestAborted);
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
        sha.AppendData(Encoding.UTF8.GetBytes(
            $"{http.Request.Method}\n{http.Request.Path.Value}\n{http.Request.QueryString.Value}\n"));
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

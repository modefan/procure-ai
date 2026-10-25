using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using ProcureAi.Gateway.Configuration;
using StackExchange.Redis;

namespace ProcureAi.Gateway.Bff;

/// <summary>
/// Keeps the signed-in sessions (the cookie ticket with the access, refresh and ID tokens) in Redis, so the browser
/// only holds a random session key. Each session is a Redis hash (<c>ticket</c>, <c>sub</c>) with an expiry that
/// follows the cookie's sliding expiry. A set per user (<c>user:{sub}</c>) lists that user's sessions, so
/// "log out everywhere" can revoke them all. The ticket is encrypted with Data Protection before it is stored.
/// </summary>
public sealed class RedisTicketStore : ITicketStore
{
    private const string TicketField = "ticket";
    private const string SubjectField = "sub";
    private const string Purpose = "ProcureAi.Gateway.Session.v1";

    private readonly IConnectionMultiplexer _redis;
    private readonly IDataProtector _protector;
    private readonly BffSessionOptions _options;
    private readonly TimeProvider _time;
    private readonly TicketSerializer _serializer = TicketSerializer.Default;

    public RedisTicketStore(
        IConnectionMultiplexer redis, IDataProtectionProvider dataProtection, IOptions<BffSessionOptions> options, TimeProvider time)
    {
        _redis = redis;
        _protector = dataProtection.CreateProtector(Purpose);
        _options = options.Value;
        _time = time;
    }

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        await WriteAsync(id, ticket);
        return id;
    }

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken) => WriteAsync(key, ticket);

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        var protectedTicket = await _redis.GetDatabase().HashGetAsync(SessionKey(key), TicketField);
        if (!protectedTicket.HasValue)
        {
            return null;
        }

        try
        {
            return _serializer.Deserialize(_protector.Unprotect((byte[])protectedTicket!));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Keys changed or the entry is damaged: no session, the user signs in again.
            return null;
        }
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        var db = _redis.GetDatabase();
        var sessionKey = SessionKey(key);
        var subject = await db.HashGetAsync(sessionKey, SubjectField);
        await db.KeyDeleteAsync(sessionKey);
        if (subject.HasValue)
        {
            await db.SetRemoveAsync(UserKey(subject!), key);
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    /// <summary>Ends every session of a user (the sessions of all their browsers). Returns how many there were.</summary>
    public async Task<int> RevokeAllForUserAsync(string subject, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var userKey = UserKey(subject);
        var sessions = await db.SetMembersAsync(userKey);
        var removed = 0;
        foreach (var session in sessions)
        {
            if (await db.KeyDeleteAsync(SessionKey(session!)))
            {
                removed++;
            }
        }

        await db.KeyDeleteAsync(userKey);
        return removed;
    }

    private async Task WriteAsync(string id, AuthenticationTicket ticket)
    {
        var subject = ticket.Principal.FindFirst("sub")?.Value;
        var db = _redis.GetDatabase();
        var sessionKey = SessionKey(id);
        var lifetime = Lifetime(ticket);

        var batch = db.CreateBatch();
        var tasks = new List<Task>
        {
            batch.HashSetAsync(sessionKey,
            [
                new HashEntry(TicketField, _protector.Protect(_serializer.Serialize(ticket))),
                new HashEntry(SubjectField, subject ?? ""),
            ]),
            batch.KeyExpireAsync(sessionKey, lifetime),
        };
        if (!string.IsNullOrEmpty(subject))
        {
            var userKey = UserKey(subject);
            tasks.Add(batch.SetAddAsync(userKey, id));
            // The index lives as long as the longest session could.
            tasks.Add(batch.KeyExpireAsync(userKey, TimeSpan.FromMinutes(_options.IdleTimeoutMinutes) + TimeSpan.FromMinutes(1)));
        }

        batch.Execute();
        await Task.WhenAll(tasks);
    }

    /// <summary>Until the ticket's expiry (the cookie's sliding expiry), plus a little so the cookie ages out first.</summary>
    private TimeSpan Lifetime(AuthenticationTicket ticket)
    {
        var fallback = TimeSpan.FromMinutes(_options.IdleTimeoutMinutes);
        if (ticket.Properties.ExpiresUtc is { } expires)
        {
            var remaining = expires - _time.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                return remaining + TimeSpan.FromMinutes(1);
            }
        }

        return fallback;
    }

    private string SessionKey(string id) => $"{_options.RedisKeyPrefix}session:{id}";

    private string UserKey(string subject) => $"{_options.RedisKeyPrefix}user:{subject}";
}

using System.ComponentModel.DataAnnotations;
using ProcureAi.BuildingBlocks.Application;

namespace ProcureAi.Notifications.Application.Inbox;

/// <summary>
/// One bell notification of a user: what happened (<c>text</c>), the request it is about and the app link to it (<c>/requests/{id}</c>),
/// when the event happened, and whether the user has read it. Pushed as <c>NotificationAdded</c> and listed by
/// <c>GET api/v1/notifications</c>.
/// </summary>
public sealed record BellItem(string Id, string Text, Guid? RequestId, string Link, DateTimeOffset CreatedAt, bool Read);

/// <summary>The bell of the caller: the newest items first and how many of them are unread.</summary>
public sealed record BellList(IReadOnlyList<BellItem> Items, int Unread);

/// <summary><c>Notifications:Bell</c>: how many items a user keeps and for how long.</summary>
public sealed class BellOptions
{
    public const string SectionName = "Notifications:Bell";

    /// <summary>Only the newest items are kept per user (older ones are dropped as new ones come). Default 50.</summary>
    [Range(1, 500)]
    public int MaxItems { get; set; } = 50;

    /// <summary>Items older than this are no longer listed, and a bell without new items for this long disappears. Default 30 days.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "365.00:00:00")]
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>The bell items of each user of each tenant (Redis: not business data, plan 04 ruling 1).</summary>
public interface IBellStore
{
    /// <summary>
    /// Adds <paramref name="item"/> unless the user already has an item with its ID (a redelivered message), then keeps only the newest
    /// <see cref="BellOptions.MaxItems"/> within the retention. True when it was added.
    /// </summary>
    Task<bool> AddAsync(string tenantId, string userId, BellItem item, CancellationToken cancellationToken);

    /// <summary>The newest items within the retention (at most <see cref="BellOptions.MaxItems"/>), with their read flags.</summary>
    Task<BellList> ListAsync(string tenantId, string userId, CancellationToken cancellationToken);

    /// <summary>Marks one item read; false when the user has no such item.</summary>
    Task<bool> MarkReadAsync(string tenantId, string userId, string itemId, CancellationToken cancellationToken);

    /// <summary>Marks every item of the user read.</summary>
    Task MarkAllReadAsync(string tenantId, string userId, CancellationToken cancellationToken);
}

/// <summary>The caller's own bell, for the API. A user only ever sees and changes the items of their own tenant and user ID.</summary>
public sealed class BellInbox(IBellStore store)
{
    public Task<BellList> ListAsync(string tenantId, string userId, CancellationToken cancellationToken) =>
        store.ListAsync(tenantId, userId, cancellationToken);

    /// <summary>Marks one item read; an item the user does not have (another user's, another tenant's) is not found.</summary>
    public async Task MarkReadAsync(string tenantId, string userId, string itemId, CancellationToken cancellationToken)
    {
        if (!await store.MarkReadAsync(tenantId, userId, itemId, cancellationToken))
        {
            throw new NotFoundException($"Notification {itemId} was not found.");
        }
    }

    public Task MarkAllReadAsync(string tenantId, string userId, CancellationToken cancellationToken) =>
        store.MarkAllReadAsync(tenantId, userId, cancellationToken);
}

namespace ProcureAi.BuildingBlocks.Application;

/// <summary>
/// The caller of the work running right now, reachable from singletons and outgoing HTTP handlers, which live outside the
/// request or message scope (an <c>HttpClient</c> handler chain is cached and shared, so it cannot hold the scoped
/// <see cref="ICallerContext"/>). Resolves to the current HTTP request's caller or, in a message handler, the message's.
/// </summary>
public interface ICallerContextAccessor
{
    ICallerContext Current { get; }
}

namespace ProcureAi.BuildingBlocks.Application;

/// <summary>Which requests and cost centers the current caller may act on or see.</summary>
public interface ICallerScope
{
    Task<CallerScope> GetAsync(CancellationToken cancellationToken);
}

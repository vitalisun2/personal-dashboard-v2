using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Tasks.Domain;

namespace PersonalDashboard.V2.Tasks.Application;

public sealed record TaskGroupOrderView(long Version, IReadOnlyList<string> Keys);

public static class TaskGroupOrderIdentity
{
    public static readonly Guid BacklogId = Guid.Parse("3be630ad-c973-4cb2-b8d3-6374d87c8355");
    public static readonly Guid TodayId = Guid.Parse("83b28844-904d-42fd-9640-c16606137f86");
    public static Guid Id(TaskLocation location) => location switch
    {
        TaskLocation.Backlog => BacklogId,
        TaskLocation.Today => TodayId,
        _ => throw new ArgumentException("Groups belong to Backlog or Today.")
    };
    public static TaskLocation? Location(Guid id) => id == BacklogId ? TaskLocation.Backlog : id == TodayId ? TaskLocation.Today : null;
}

public interface ITaskGroupOrderRepository
{
    Task<TaskGroupOrderView> ReadGroupOrderAsync(TaskLocation location, CancellationToken ct);
    Task SaveGroupOrderAsync(TaskLocation location, long expectedVersion, IReadOnlyList<string> keys, CancellationToken ct);
}

public sealed class TaskGroupOrderService(ITasksRepository tasks, ITaskGroupOrderRepository orders, ITransactionRunner transaction)
{
    public async Task<TaskGroupOrderView> GetAsync(TaskLocation location, CancellationToken ct)
    {
        EnsureLocation(location);
        var saved = await orders.ReadGroupOrderAsync(location, ct);
        var current = await CurrentKeysAsync(location, ct);
        var visible = saved.Keys.Where(current.Contains).Distinct().ToList();
        visible.AddRange(current.Where(key => !visible.Contains(key)));
        return new TaskGroupOrderView(saved.Version, visible);
    }

    public Task<TaskGroupOrderView> ReorderAsync(TaskLocation location, long expectedVersion, IReadOnlyList<string> keys, CancellationToken ct) => transaction.ExecuteAsync(async token =>
    {
        EnsureLocation(location);
        var current = await CurrentKeysAsync(location, token);
        if (keys.Count != current.Count || keys.Distinct().Count() != keys.Count || !keys.ToHashSet().SetEquals(current))
            throw new ArgumentException("Order must include every current task group exactly once.");
        await orders.SaveGroupOrderAsync(location, expectedVersion, keys, token);
        return new TaskGroupOrderView(expectedVersion + 1, keys.ToArray());
    }, ct);

    private async Task<IReadOnlyList<string>> CurrentKeysAsync(TaskLocation location, CancellationToken ct)
    {
        var sections = await tasks.ListSectionsAsync(TaskLocation.Backlog, ct);
        var items = await tasks.ListAsync(location, ct);
        var sectionIds = location == TaskLocation.Backlog
            ? sections.Where(section => section.IsBacklogVisible).Select(section => section.Id).ToHashSet()
            : items.Where(item => item.SectionId is not null).Select(item => item.SectionId!.Value).ToHashSet();
        return sections.Where(section => sectionIds.Contains(section.Id)).OrderBy(x => x.Position).Select(x => $"section:{x.Id}")
            .Concat(items.Where(x => x.ProjectId is not null).Select(x => x.ProjectId!.Value).Distinct().Select(id => $"project:{id}"))
            .ToArray();
    }

    private static void EnsureLocation(TaskLocation location)
    {
        if (location is not (TaskLocation.Backlog or TaskLocation.Today)) throw new ArgumentException("Groups belong to Backlog or Today.");
    }
}

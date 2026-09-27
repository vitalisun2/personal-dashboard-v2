using System.Text.Json;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;
using PersonalDashboard.V2.Tasks.Infrastructure;
using Xunit;

namespace PersonalDashboard.V2.Host.Tests;

public sealed class TaskGroupOrderTests
{
    [Fact]
    public async Task Project_group_can_be_moved_between_plain_sections_and_order_survives_reload()
    {
        var first = new TaskSection("First", TaskLocation.Backlog, position: 0);
        var second = new TaskSection("Second", TaskLocation.Backlog, position: 1);
        var projectId = Guid.NewGuid();
        var linked = new TaskItem("Linked", projectId: projectId, milestoneId: Guid.NewGuid(), featureId: Guid.NewGuid());
        linked.MoveToBacklog(null);
        var tasks = new FakeTasksRepository([first, second], [linked]);
        var orders = new FakeGroupOrderRepository();
        var service = new TaskGroupOrderService(tasks, orders, new ImmediateTransactionRunner());

        var initial = await service.GetAsync(TaskLocation.Backlog, CancellationToken.None);
        Assert.Equal([$"section:{first.Id}", $"section:{second.Id}", $"project:{projectId}"], initial.Keys);

        var moved = await service.ReorderAsync(TaskLocation.Backlog, initial.Version,
            [$"section:{first.Id}", $"project:{projectId}", $"section:{second.Id}"], CancellationToken.None);
        Assert.Equal(1, moved.Version);
        var reloaded = await service.GetAsync(TaskLocation.Backlog, CancellationToken.None);
        Assert.Equal(moved.Version, reloaded.Version);
        Assert.Equal(moved.Keys, reloaded.Keys);
    }

    [Fact]
    public async Task Reorder_rejects_missing_groups_and_stale_version()
    {
        var section = new TaskSection("First", TaskLocation.Backlog);
        var projectId = Guid.NewGuid();
        var linked = new TaskItem("Linked", projectId: projectId, milestoneId: Guid.NewGuid(), featureId: Guid.NewGuid());
        linked.MoveToBacklog(null);
        var service = new TaskGroupOrderService(new FakeTasksRepository([section], [linked]), new FakeGroupOrderRepository(), new ImmediateTransactionRunner());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReorderAsync(TaskLocation.Backlog, 0, [$"section:{section.Id}"], CancellationToken.None));
        await service.ReorderAsync(TaskLocation.Backlog, 0, [$"project:{projectId}", $"section:{section.Id}"], CancellationToken.None);
        await Assert.ThrowsAsync<TaskVersionConflictException>(() => service.ReorderAsync(TaskLocation.Backlog, 0, [$"section:{section.Id}", $"project:{projectId}"], CancellationToken.None));
    }

    [Fact]
    public async Task Offline_group_order_operation_applies_and_returns_new_snapshot()
    {
        var section = new TaskSection("First", TaskLocation.Backlog);
        var projectId = Guid.NewGuid();
        var linked = new TaskItem("Linked", projectId: projectId, milestoneId: Guid.NewGuid(), featureId: Guid.NewGuid());
        linked.MoveToBacklog(null);
        var service = new TaskGroupOrderService(new FakeTasksRepository([section], [linked]), new FakeGroupOrderRepository(), new ImmediateTransactionRunner());
        var handler = new TaskGroupOrderSyncMutationHandler(service);
        var keys = new[] { $"project:{projectId}", $"section:{section.Id}" };
        var operation = new SyncOperation(Guid.NewGuid(), handler.Type, TaskGroupOrderIdentity.BacklogId, 0,
            SyncOperationKind.Upsert, JsonSerializer.SerializeToElement(new { keys }));

        var result = await handler.ApplyAsync(operation);
        Assert.True(result.Applied);
        Assert.Equal(1, result.Current?.Version);
        Assert.Equal(keys, (await service.GetAsync(TaskLocation.Backlog, CancellationToken.None)).Keys);
        Assert.False((await handler.ApplyAsync(operation)).Applied);
    }

    private sealed class FakeTasksRepository(IReadOnlyList<TaskSection> sections, IReadOnlyList<TaskItem> items) : ITasksRepository
    {
        public Task<IReadOnlyList<TaskItem>> ListAsync(TaskLocation? location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskItem>>(items.Where(x => x.Location == location).ToArray());
        public Task<IReadOnlyList<TaskSection>> ListSectionsAsync(TaskLocation location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskSection>>(sections.Where(x => x.Location == location).ToArray());
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveAsync(TaskItem item, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveSectionAsync(TaskSection section, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteSectionAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class FakeGroupOrderRepository : ITaskGroupOrderRepository
    {
        private readonly Dictionary<TaskLocation, TaskGroupOrderView> values = [];
        public Task<TaskGroupOrderView> ReadGroupOrderAsync(TaskLocation location, CancellationToken ct) => Task.FromResult(values.GetValueOrDefault(location) ?? new TaskGroupOrderView(0, []));
        public Task SaveGroupOrderAsync(TaskLocation location, long expectedVersion, IReadOnlyList<string> keys, CancellationToken ct)
        {
            var actual = values.GetValueOrDefault(location)?.Version ?? 0;
            if (actual != expectedVersion) throw new TaskVersionConflictException(actual, expectedVersion);
            values[location] = new(expectedVersion + 1, keys.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateTransactionRunner : ITransactionRunner
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
}

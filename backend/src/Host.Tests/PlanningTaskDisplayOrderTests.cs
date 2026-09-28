using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;
using PersonalDashboard.V2.Tasks.Infrastructure;
using Xunit;

namespace PersonalDashboard.V2.Host.Tests;

public sealed class PlanningTaskDisplayOrderTests
{
    [Fact]
    public async Task Linked_backlog_task_can_be_reordered_without_changing_placement()
    {
        var projectId = Guid.NewGuid();
        var milestoneId = Guid.NewGuid();
        var featureId = Guid.NewGuid();
        var planned = new TaskItem("Planned", projectId: projectId, milestoneId: milestoneId, featureId: featureId);
        var backlog = new TaskItem("Backlog", projectId: projectId, milestoneId: milestoneId, featureId: featureId);
        backlog.MoveToBacklog(null);
        var service = new TasksService(new TasksMemory([planned, backlog]), new ValidLinks(), new Transactions());
        var order = new[] { new TaskOrderItem(backlog.Id, backlog.Version), new TaskOrderItem(planned.Id, planned.Version) };

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReorderTasksAsync(TaskLocation.Planned, null, order, CancellationToken.None, projectId, milestoneId, featureId));
        var result = await service.ReorderTasksAsync(TaskLocation.Planned, null, order, CancellationToken.None, projectId, milestoneId, featureId, planningDisplayOrder: true);

        Assert.Equal([backlog.Id, planned.Id], result.Select(task => task.Id));
        Assert.Equal([0, 1], result.Select(task => task.PlanningPosition));
        Assert.Equal([0, 0], result.Select(task => task.Position));
        Assert.Equal(TaskLocation.Backlog, backlog.Location);
        Assert.Equal(TaskLocation.Planned, planned.Location);

        var synced = await new TasksAgentAccess(service).ApplyAsync(new TaskMutation(TaskMutationKind.Reorder, TaskEntityKind.Task,
            planned.Id, planned.Version, Planning: new PlanningLink(projectId, milestoneId, featureId), Placement: "planningDisplay",
            Order: [new(planned.Id, planned.Version), new(backlog.Id, backlog.Version)]));
        Assert.True(synced.Applied, synced.ConflictReason);
        Assert.Equal([0, 1], new[] { planned.PlanningPosition, backlog.PlanningPosition });
        Assert.Equal([0, 0], new[] { planned.Position, backlog.Position });
    }

    private sealed class ValidLinks : IPlanningLinkValidator
    {
        public Task<PlanningLinkValidationResult> ValidateAsync(PlanningLink link, CancellationToken ct = default) => Task.FromResult(new PlanningLinkValidationResult(true, []));
    }

    private sealed class Transactions : ITransactionRunner
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default) => operation(ct);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default) => operation(ct);
    }

    private sealed class TasksMemory(List<TaskItem> items) : ITasksRepository
    {
        public Task<IReadOnlyList<TaskItem>> ListAsync(TaskLocation? location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskItem>>(items.Where(task => location is null || task.Location == location).ToArray());
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(items.SingleOrDefault(task => task.Id == id));
        public Task SaveAsync(TaskItem item, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TaskSection>> ListSectionsAsync(TaskLocation location, CancellationToken ct) => throw new NotImplementedException();
        public Task SaveSectionAsync(TaskSection section, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteSectionAsync(Guid id, CancellationToken ct) => throw new NotImplementedException();
    }
}

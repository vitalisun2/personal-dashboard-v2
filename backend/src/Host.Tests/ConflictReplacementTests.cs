using System.Text.Json;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Planning.Application;
using PersonalDashboard.V2.Planning.Domain;
using PersonalDashboard.V2.Planning.Infrastructure;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;
using PersonalDashboard.V2.Tasks.Infrastructure;
using Xunit;

namespace PersonalDashboard.V2.Host.Tests;

public sealed class ConflictReplacementTests
{
    [Fact]
    public async Task Synced_task_replacement_restores_all_fields_atomically_and_checks_server_version()
    {
        var repository = new TasksMemory();
        var section = new TaskSection("Today section", TaskLocation.Today);
        repository.Sections.Add(section);
        var task = new TaskItem("Server", "Server body", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        task.Archive(); repository.Items.Add(task);
        var handler = new TaskSyncMutationHandler(new TasksAgentAccess(new TasksService(repository, new Links(), new Transactions())));
        var version = task.Version;
        var operation = TaskOperation(task.Id, version, new { operation = "replace", kind = "task", id = task.Id, expectedVersion = version,
            title = "Local title", description = "Local body", planning = (object?)null, placement = "today", workStatus = "inProgress", sectionId = section.Id, position = 3 });

        var applied = await handler.ApplyAsync(operation);
        Assert.True(applied.Applied, applied.ConflictReason);
        Assert.Equal(version + 1, task.Version);
        Assert.Equal("Local title", task.Title); Assert.Equal("Local body", task.Description);
        Assert.Null(task.ProjectId); Assert.Null(task.FeatureId);
        Assert.Equal(TaskLocation.Today, task.Location); Assert.Equal(TaskWorkStatus.InProgress, task.WorkStatus);
        Assert.Equal(section.Id, task.SectionId); Assert.Equal(3, task.Position);
        var stale = await handler.ApplyAsync(operation with { OperationId = Guid.NewGuid() });
        Assert.False(stale.Applied); Assert.Equal(version + 1, stale.Current!.Version);

        var archived = await handler.ApplyAsync(TaskOperation(task.Id, task.Version, new { operation = "replace", kind = "task", id = task.Id,
            title = "Archived local", description = "Archive body", placement = "archived", workStatus = "done", archivedSectionName = "Old section" }));
        Assert.True(archived.Applied, archived.ConflictReason);
        Assert.Equal(TaskLocation.Archived, task.Location); Assert.Equal(TaskWorkStatus.Done, task.WorkStatus);
        Assert.Null(task.SectionId); Assert.Equal("Old section", task.ArchivedSectionName);
    }

    [Theory]
    [InlineData("planned", "new", false)]
    [InlineData("backlog", "done", true)]
    [InlineData("today", "new", true)]
    [InlineData("999", "new", false)]
    [InlineData("archived", "999", false)]
    public async Task Invalid_task_replacement_never_partially_changes_title_or_version(string placement, string status, bool withSection)
    {
        var repository = new TasksMemory();
        var section = new TaskSection("Backlog", TaskLocation.Backlog); repository.Sections.Add(section);
        var task = new TaskItem("Original", "Original body", sectionId: section.Id); repository.Items.Add(task);
        var handler = new TaskSyncMutationHandler(new TasksAgentAccess(new TasksService(repository, new Links(), new Transactions())));
        var result = await handler.ApplyAsync(TaskOperation(task.Id, task.Version, new { operation = "replace", kind = "task", id = task.Id,
            title = "Must not save", description = "Must not save", placement, workStatus = status, sectionId = withSection ? (Guid?)section.Id : null }));
        Assert.False(result.Applied); Assert.Equal("Original", task.Title); Assert.Equal("Original body", task.Description);
        Assert.Equal(1, task.Version); Assert.Equal(section.Id, task.SectionId);
    }

    [Fact]
    public async Task Missing_task_replacement_is_a_conflict_instead_of_recreating_deleted_data()
    {
        var access = new TasksAgentAccess(new TasksService(new TasksMemory(), new Links(), new Transactions()));
        var result = await access.ApplyAsync(new TaskMutation(TaskMutationKind.Replace, TaskEntityKind.Task, Guid.NewGuid(), 5,
            "Local", Placement: "archived", WorkStatus: "new"));
        Assert.False(result.Applied); Assert.Null(result.Current);
    }

    [Fact]
    public async Task Planning_replacement_changes_only_own_fields_and_keeps_server_children()
    {
        var project = new Project("Server project");
        var milestone = project.AddMilestone("Server epic");
        var feature = milestone.AddFeature("Server feature");
        var sibling = milestone.AddFeature("New server feature");
        var repository = new PlanningMemory(project);
        var access = new PlanningAgentAccess(new PlanningService(repository, new Links(), new Links(), new Transactions()));
        var version = project.Version;
        var handler = new PlanningProjectSyncHandler(access);
        var result = await handler.ApplyAsync(new SyncOperation(Guid.NewGuid(), "planning.project", project.Id, version, SyncOperationKind.Upsert,
            JsonSerializer.SerializeToElement(new { operation = "replace", kind = "project", id = project.Id, title = "Local project", description = "Local body", isArchived = true })));
        Assert.True(result.Applied, result.ConflictReason);
        Assert.Equal("Local project", project.Title); Assert.True(project.IsArchived); Assert.Equal(version + 1, project.Version);
        Assert.Same(milestone, Assert.Single(project.Milestones)); Assert.Equal(2, milestone.Features.Count);
        var staleProject = await access.ApplyAsync(new PlanningMutation(PlanningMutationKind.Replace, PlanningEntityKind.Project,
            project.Id, null, null, version, "Stale project", IsArchived: false));
        Assert.False(staleProject.Applied); Assert.Equal("Local project", project.Title); Assert.True(project.IsArchived);

        var epicVersion = milestone.Version;
        var epic = await access.ApplyAsync(new PlanningMutation(PlanningMutationKind.Replace, PlanningEntityKind.Milestone, milestone.Id,
            project.Id, null, milestone.Version, "Local epic", "Epic body"));
        Assert.True(epic.Applied, epic.ConflictReason); Assert.Equal("Local epic", milestone.Title);
        Assert.Equal(2, milestone.Features.Count); Assert.Equal("New server feature", sibling.Title);
        var staleEpic = await access.ApplyAsync(new PlanningMutation(PlanningMutationKind.Replace, PlanningEntityKind.Milestone, milestone.Id,
            project.Id, null, epicVersion, "Stale epic"));
        Assert.False(staleEpic.Applied); Assert.Equal("Local epic", milestone.Title);

        var oldVersion = feature.Version;
        var replacement = new PlanningMutation(PlanningMutationKind.Replace, PlanningEntityKind.Feature, feature.Id,
            project.Id, milestone.Id, oldVersion, "Local feature", "Feature body", "done");
        var changed = await access.ApplyAsync(replacement);
        Assert.True(changed.Applied, changed.ConflictReason); Assert.Equal(FeatureStatus.Done, feature.Status);
        Assert.Equal("Local feature", feature.Title); Assert.Equal(oldVersion + 1, feature.Version);
        var stale = await access.ApplyAsync(replacement with { Title = "Stale overwrite" });
        Assert.False(stale.Applied); Assert.Equal("Local feature", feature.Title);
        var invalid = await access.ApplyAsync(replacement with { ExpectedVersion = feature.Version, Title = "Invalid overwrite", FeatureStatus = "999" });
        Assert.False(invalid.Applied); Assert.Equal("Local feature", feature.Title); Assert.Equal(oldVersion + 1, feature.Version);
    }

    private static SyncOperation TaskOperation(Guid id, long version, object payload) => new(Guid.NewGuid(), "tasks.task", id, version, SyncOperationKind.Upsert, JsonSerializer.SerializeToElement(payload));
    private sealed class Transactions : ITransactionRunner
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class Links : IPlanningLinkValidator, ITaskPlanningLinkUsage, ITaskPlanningProjectionRefresh
    {
        public Task<PlanningLinkValidationResult> ValidateAsync(PlanningLink link, CancellationToken cancellationToken = default) => Task.FromResult(new PlanningLinkValidationResult(true, []));
        public Task<bool> IsInUseAsync(PlanningLink link, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task RefreshAsync(PlanningLink changedAncestor, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class TasksMemory : ITasksRepository
    {
        public List<TaskItem> Items { get; } = [];
        public List<TaskSection> Sections { get; } = [];
        public Task<IReadOnlyList<TaskItem>> ListAsync(TaskLocation? location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskItem>>(Items.Where(x => location is null || x.Location == location).ToArray());
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.SingleOrDefault(x => x.Id == id));
        public Task SaveAsync(TaskItem item, CancellationToken ct) { if (!Items.Contains(item)) Items.Add(item); return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken ct) { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
        public Task<IReadOnlyList<TaskSection>> ListSectionsAsync(TaskLocation location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskSection>>(Sections.Where(x => x.Location == location).ToArray());
        public Task SaveSectionAsync(TaskSection section, CancellationToken ct) { if (!Sections.Contains(section)) Sections.Add(section); return Task.CompletedTask; }
        public Task DeleteSectionAsync(Guid id, CancellationToken ct) { Sections.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    }
    private sealed class PlanningMemory(Project project) : IPlanningRepository
    {
        public Task<IReadOnlyList<Project>> ListProjectsAsync(bool includeArchived, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Project>>([project]);
        public Task<Project?> GetProjectAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(id == project.Id ? project : null);
        public Task<bool> EntityIdExistsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(id == project.Id);
        public Task SaveAsync(Project item, CancellationToken cancellationToken, IReadOnlyList<PlanningDeletion>? deleted = null) => Task.CompletedTask;
        public Task DeleteProjectAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

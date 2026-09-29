using PersonalDashboard.V2.Tasks.Domain;
using PersonalDashboard.V2.Contracts.Planning;

namespace PersonalDashboard.V2.Tasks.Application;

public sealed record TaskView(Guid Id, string Title, string Description, Guid? ProjectId, Guid? MilestoneId, Guid? FeatureId, TaskLocation Location, TaskWorkStatus WorkStatus, Guid? SectionId, int Position, long Version, string? ArchivedSectionName = null, int PlanningPosition = 0);
public sealed record TaskSectionView(Guid Id, string Name, TaskLocation Location, int Position, long Version, bool IsBacklogVisible = true);
public sealed record TaskOrderItem(Guid Id, long ExpectedVersion);

public interface ITasksRepository
{
    Task<IReadOnlyList<TaskItem>> ListAsync(TaskLocation? location, CancellationToken ct);
    Task<TaskItem?> GetAsync(Guid id, CancellationToken ct);
    Task SaveAsync(TaskItem item, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<TaskSection>> ListSectionsAsync(TaskLocation location, CancellationToken ct);
    Task SaveSectionAsync(TaskSection section, CancellationToken ct);
    Task DeleteSectionAsync(Guid id, CancellationToken ct);
}

public sealed class TasksService(ITasksRepository repository, PersonalDashboard.V2.Contracts.Planning.IPlanningLinkValidator planning, PersonalDashboard.V2.Contracts.Transactions.ITransactionRunner transaction)
{
    public async Task<IReadOnlyList<TaskView>> ListAsync(TaskLocation? location, CancellationToken ct) => (await repository.ListAsync(location, ct)).OrderBy(t => t.Position).Select(Map).ToArray();
    public async Task<TaskView?> GetAsync(Guid id, CancellationToken ct) => await repository.GetAsync(id, ct) is { } item ? Map(item) : null;

    public async Task<TaskView> CreateAsync(string title, string? description, Guid? projectId, Guid? milestoneId, Guid? featureId, Guid? sectionId, CancellationToken ct, Guid? id = null)
    {
        var validation = await planning.ValidateAsync(new PlanningLink(projectId, milestoneId, featureId), ct);
        if (!validation.IsValid) throw new InvalidPlanningLinkException();
        if (projectId is null) sectionId = await ResolveSection(TaskLocation.Backlog, sectionId, null, ct);
        else sectionId = null;
        var item = new TaskItem(title, description, projectId, milestoneId, featureId, sectionId, id); await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task<TaskView> EditAsync(Guid id, long expectedVersion, string title, string? description, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion); item.Edit(title, description); await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task<TaskView> UpdateMutationAsync(Guid id, long expectedVersion, string? title, string? description, Guid? sectionId, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion);
        if (sectionId is not null)
        {
            if (item.ProjectId is not null || item.Location is not (TaskLocation.Backlog or TaskLocation.Today)) throw new InvalidOperationException("Only standalone Backlog or Today tasks have editable sections.");
            var section = await FindSection(sectionId.Value, ct);
            if (section.Location != TaskLocation.Backlog) throw new ArgumentException("Task section is invalid.");
            if (item.Location == TaskLocation.Backlog && !section.IsBacklogVisible) throw new ArgumentException("Choose a section visible in Backlog.");
        }
        item.Update(title, description, sectionId); await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task<TaskView> SetSectionAsync(Guid id, long expectedVersion, Guid sectionId, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion);
        if (item.ProjectId is not null || item.Location is not (TaskLocation.Backlog or TaskLocation.Today)) throw new InvalidOperationException("Only standalone Backlog or Today tasks have editable sections.");
        var section = await FindSection(sectionId, ct);
        if (item.Location == TaskLocation.Backlog && !section.IsBacklogVisible) throw new ArgumentException("Choose a section visible in Backlog.");
        item.SetSection(sectionId); await repository.SaveAsync(item, ct); return Map(item);
    }

    public Task<TaskView> ReplaceAsync(Guid id, long expectedVersion, string title, string? description, PlanningLink? link,
        TaskLocation location, TaskWorkStatus status, Guid? sectionId, int? position, string? archivedSectionName, CancellationToken ct)
        => transaction.ExecuteAsync(async token =>
        {
            var item = await Required(id, token); Check(item.Version, expectedVersion);
            if (link is not null && !(await planning.ValidateAsync(link, token)).IsValid) throw new InvalidPlanningLinkException();
            if (sectionId is { } section)
            {
                var destination = await FindSection(section, token);
        if (destination.Location != TaskLocation.Backlog || location == TaskLocation.Backlog && !destination.IsBacklogVisible) throw new ArgumentException("Task section is invalid.");
            }
            item.Replace(title, description, link?.ProjectId, link?.MilestoneId, link?.FeatureId, location, status,
                sectionId, position ?? item.Position, archivedSectionName);
            await repository.SaveAsync(item, token);
            return Map(item);
        }, ct);

    public async Task<TaskView> UpdateWithPlanningLinkAsync(Guid id, long expectedVersion, string? title, string? description, PlanningLink link, bool moveToPlanning, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion);
        var validation = await planning.ValidateAsync(link, ct);
        if (!validation.IsValid || link.ProjectId is not { } projectId || link.MilestoneId is not { } milestoneId || link.FeatureId is not { } featureId)
            throw new InvalidPlanningLinkException();
        item.UpdateWithPlanningLink(title, description, projectId, milestoneId, featureId, moveToPlanning);
        await repository.SaveAsync(item, ct); return Map(item);
    }

    public async Task<IReadOnlyList<TaskView>> ReorderTasksAsync(TaskLocation location, Guid? sectionId, IReadOnlyList<TaskOrderItem> order, CancellationToken ct, Guid? projectId = null, Guid? milestoneId = null, Guid? featureId = null, bool planningDisplayOrder = false)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            if (location is not (TaskLocation.Planned or TaskLocation.Backlog or TaskLocation.Today)) throw new ArgumentException("Only planned, Backlog or Today tasks can be reordered.");
            if (planningDisplayOrder && location != TaskLocation.Planned) throw new ArgumentException("Planning display order requires planned scope.");
            IReadOnlyList<TaskItem> candidates = await repository.ListAsync(planningDisplayOrder ? null : location, token);
            TaskItem[] current;
            if (location == TaskLocation.Planned)
            {
                if (featureId is null || sectionId is not null || projectId is null || milestoneId is null) throw new ArgumentException("Planned task order requires project, milestone and feature scope.");
                current = candidates.Where(t => t.ProjectId == projectId && t.MilestoneId == milestoneId && t.FeatureId == featureId).ToArray();
            }
            else if (projectId is not null)
            {
                if (sectionId is not null || milestoneId is not null || featureId is not null) throw new ArgumentException("Linked Backlog or Today task order is scoped to one project.");
                current = candidates.Where(t => t.ProjectId == projectId).ToArray();
            }
            else
            {
                if (sectionId is null || milestoneId is not null || featureId is not null) throw new ArgumentException("Standalone task order requires a section, or a linked order requires a project.");
                current = candidates.Where(t => t.ProjectId is null && t.SectionId == sectionId).ToArray();
            }
            if (order.Count != current.Length || order.Select(x => x.Id).Distinct().Count() != order.Count || !order.Select(x => x.Id).ToHashSet().SetEquals(current.Select(x => x.Id))) throw new ArgumentException("Order must include every current task in this scope exactly once.");
            foreach (var (entry, position) in order.Select((entry, position) => (entry, position)))
            {
                var task = current.Single(x => x.Id == entry.Id); Check(task.Version, entry.ExpectedVersion);
                if (planningDisplayOrder) task.SetPlanningPosition(position);
                else task.SetPosition(position);
                await repository.SaveAsync(task, token);
            }
            return order.Select(entry => Map(current.Single(x => x.Id == entry.Id))).ToArray();
        }, ct);
    }

    public async Task<TaskView> MoveToTodayAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion); Guid? sectionId = item.ProjectId is null ? item.SectionId ?? await ResolveSection(TaskLocation.Backlog, null, null, ct) : null; item.MoveToToday(sectionId); await repository.SaveAsync(item, ct); return Map(item);
    }
    public Task<TaskView> MoveToBacklogAsync(Guid id, long expectedVersion, CancellationToken ct) => transaction.ExecuteAsync(async token =>
    {
        var item = await Required(id, token); Check(item.Version, expectedVersion);
        Guid? sectionId = item.ProjectId is null ? item.SectionId ?? await ResolveSection(TaskLocation.Backlog, null, null, token) : null;
        item.MoveToBacklog(sectionId);
        var section = sectionId is { } sectionKey ? await FindSection(sectionKey, token) : null;
        var sectionWasHidden = section is { IsBacklogVisible: false };
        section?.ShowInBacklog();
        await repository.SaveAsync(item, token);
        if (sectionWasHidden) await repository.SaveSectionAsync(section!, token);
        return Map(item);
    }, ct);
    public Task<TaskView> ReturnToPlanAsync(Guid id, long expectedVersion, CancellationToken ct) => Mutate(id, expectedVersion, x => x.ReturnToPlan(), ct);
    public Task<TaskView> SetStatusAsync(Guid id, long expectedVersion, TaskWorkStatus status, CancellationToken ct) => Mutate(id, expectedVersion, x => x.SetWorkStatus(status), ct);
    public async Task<TaskView> ArchiveAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, expectedVersion);
        var sectionName = item.SectionId is { } sectionId ? (await FindSection(sectionId, ct)).Name : null;
        item.Archive(sectionName); await repository.SaveAsync(item, ct); return Map(item);
    }
    public Task<TaskView> RestoreAsync(Guid id, long expectedVersion, Guid? sectionId, CancellationToken ct) => transaction.ExecuteAsync(async token =>
    {
        var item = await Required(id, token); Check(item.Version, expectedVersion);
        if (item.ProjectId is null) sectionId = item.SectionId ?? await ResolveSection(TaskLocation.Backlog, sectionId, null, token, item.ArchivedSectionName);
        item.Restore(sectionId);
        var section = sectionId is { } restoredSection ? await FindSection(restoredSection, token) : null;
        var sectionWasHidden = section is { IsBacklogVisible: false };
        section?.ShowInBacklog();
        await repository.SaveAsync(item, token);
        if (sectionWasHidden) await repository.SaveSectionAsync(section!, token);
        return Map(item);
    }, ct);

    public async Task DeleteAsync(Guid id, long expectedVersion, CancellationToken ct) { var item = await Required(id, ct); Check(item.Version, expectedVersion); await repository.DeleteAsync(id, ct); }

    public async Task<IReadOnlyList<TaskSectionView>> SectionsAsync(TaskLocation location, CancellationToken ct)
    {
        if (location is not (TaskLocation.Backlog or TaskLocation.Today or TaskLocation.Archived)) throw new ArgumentException("Sections are available for Backlog, Today or Archived.");
        var sections = await repository.ListSectionsAsync(TaskLocation.Backlog, ct);
        if (location == TaskLocation.Backlog) return sections.Where(s => s.IsBacklogVisible).OrderBy(s => s.Position).Select(s => Map(s, location)).ToArray();
        var referencedIds = (await repository.ListAsync(location, ct)).Where(task => task.SectionId is not null).Select(task => task.SectionId!.Value).ToHashSet();
        return sections.Where(section => referencedIds.Contains(section.Id)).OrderBy(s => s.Position).Select(s => Map(s, location)).ToArray();
    }

    public async Task<IReadOnlyList<TaskSectionView>> AllSectionsAsync(CancellationToken ct) =>
        (await repository.ListSectionsAsync(TaskLocation.Backlog, ct)).OrderBy(section => section.Position).Select(section => Map(section, TaskLocation.Backlog)).ToArray();

    public async Task<TaskSectionView> CreateSectionAsync(string name, TaskLocation location, CancellationToken ct, Guid? id = null)
    {
        EnsureSectionLocation(location);
        var sections = await repository.ListSectionsAsync(TaskLocation.Backlog, ct);
        var section = new TaskSection(name, TaskLocation.Backlog, id, sections.Count);
        var normalizedName = section.Name;
        var existing = sections.FirstOrDefault(candidate => candidate.Name == normalizedName);
        if (existing is not null)
        {
            if (id is { } requestedId && requestedId != existing.Id)
                throw new InvalidOperationException($"A section named '{normalizedName}' already exists.");
            if (!existing.IsBacklogVisible)
            {
                existing.ShowInBacklog();
                await repository.SaveSectionAsync(existing, ct);
            }
            return Map(existing, location);
        }

        await repository.SaveSectionAsync(section, ct);
        return Map(section, location);
    }

    public async Task<TaskSectionView> RenameSectionAsync(Guid id, long expectedVersion, string name, CancellationToken ct)
    {
        var section = await FindSection(id, ct); Check(section.Version, expectedVersion); section.Rename(name); await repository.SaveSectionAsync(section, ct); return Map(section);
    }

    public async Task<IReadOnlyList<TaskSectionView>> ReorderSectionsAsync(TaskLocation location, long expectedVersion, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        return await transaction.ExecuteAsync(async token =>
        {
            EnsureSectionLocation(location); var list = (await repository.ListSectionsAsync(TaskLocation.Backlog, token)).Where(s => s.IsBacklogVisible).OrderBy(s => s.Position).ToList(); Check(list.Count == 0 ? 0 : list.Max(s => s.Version), expectedVersion);
            if (ids.Count != list.Count || ids.Distinct().Count() != ids.Count || !ids.ToHashSet().SetEquals(list.Select(s => s.Id))) throw new ArgumentException("Order must include every current section exactly once.");
            for (var i = 0; i < ids.Count; i++) { var section = list.Single(x => x.Id == ids[i]); section.Reorder(i); await repository.SaveSectionAsync(section, token); }
            return await SectionsAsync(location, token);
        }, ct);
    }

    public async Task DeleteSectionAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        await transaction.ExecuteAsync(async token =>
        {
            var section = await FindSection(id, token); Check(section.Version, expectedVersion);
            if (!section.IsBacklogVisible) throw new InvalidOperationException("Only a section visible in Backlog can be removed from Backlog.");
            var backlogTasks = (await repository.ListAsync(TaskLocation.Backlog, token)).Where(task => task.SectionId == id).ToArray();
            foreach (var task in backlogTasks) await ArchiveAsync(task.Id, task.Version, token);
            section.HideFromBacklog();
            var hasTasks = (await repository.ListAsync(null, token)).Any(task => task.SectionId == id);
            if (!hasTasks) await repository.DeleteSectionAsync(id, token);
            else await repository.SaveSectionAsync(section, token);
        }, ct);
    }

    public async Task DeleteArchivedSectionAsync(Guid id, long expectedVersion, CancellationToken ct)
    {
        await transaction.ExecuteAsync(async token =>
        {
            var section = await FindSection(id, token); Check(section.Version, expectedVersion);
            var tasks = (await repository.ListAsync(null, token)).Where(task => task.SectionId == id).ToArray();
            if (tasks.Any(task => task.Location is TaskLocation.Backlog or TaskLocation.Today)) throw new InvalidOperationException("Move active tasks before deleting the archived section.");
            if (!tasks.Any(task => task.Location == TaskLocation.Archived)) throw new InvalidOperationException("The section has no archived tasks to delete.");
            foreach (var task in tasks) await repository.DeleteAsync(task.Id, token);
            if ((await repository.ListSectionsAsync(TaskLocation.Backlog, token)).Any(item => item.Id == id))
                await repository.DeleteSectionAsync(id, token);
        }, ct);
    }

    private async Task<TaskView> Mutate(Guid id, long version, Action<TaskItem> action, CancellationToken ct)
    {
        var item = await Required(id, ct); Check(item.Version, version); action(item); await repository.SaveAsync(item, ct); return Map(item);
    }
    private async Task<TaskItem> Required(Guid id, CancellationToken ct) => await repository.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Task {id} was not found.");
    private async Task<TaskSection> FindSection(Guid id, CancellationToken ct) => (await repository.ListSectionsAsync(TaskLocation.Backlog, ct)).SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Section {id} was not found.");
    private async Task<Guid> ResolveSection(TaskLocation destination, Guid? requestedId, Guid? previousId, CancellationToken ct, string? previousName = null)
    {
        var sections = (await repository.ListSectionsAsync(TaskLocation.Backlog, ct)).OrderBy(x => x.Position).ToArray();
        if (requestedId is { } wanted)
        {
            var requested = sections.SingleOrDefault(x => x.Id == wanted) ?? throw new ArgumentException("Section does not exist.");
            if (destination == TaskLocation.Backlog && !requested.IsBacklogVisible) throw new ArgumentException("Choose a section visible in Backlog.");
            return requested.Id;
        }
        if (previousId is { } oldId) { try { previousName = (await FindSection(oldId, ct)).Name; } catch (KeyNotFoundException) { } }
        if (previousName is not null && sections.FirstOrDefault(x => x.Name == previousName) is { } match) return match.Id;
        if ((destination == TaskLocation.Backlog ? sections.FirstOrDefault(x => x.IsBacklogVisible) : sections.FirstOrDefault()) is { } first) return first.Id;
        var baseName = previousName ?? "Общее";
        var newName = baseName;
        for (var suffix = 2; sections.Any(section => section.Name == newName); suffix++) newName = $"{baseName} {suffix}";
        return (await CreateSectionAsync(newName, TaskLocation.Backlog, ct)).Id;
    }
    private static void Check(long actual, long expected) { if (actual != expected) throw new TaskVersionConflictException(actual, expected); }
    private static void EnsureSectionLocation(TaskLocation location) { if (location != TaskLocation.Backlog) throw new ArgumentException("Sections can only be created in Backlog."); }
    private static TaskView Map(TaskItem x) => new(x.Id, x.Title, x.Description, x.ProjectId, x.MilestoneId, x.FeatureId, x.Location, x.WorkStatus, x.SectionId, x.Position, x.Version, x.ArchivedSectionName, x.PlanningPosition);
    private static TaskSectionView Map(TaskSection x, TaskLocation? location = null) => new(x.Id, x.Name, location ?? x.Location, x.Position, x.Version, x.IsBacklogVisible);
}

public sealed class InvalidPlanningLinkException() : Exception("The project, milestone and feature link is invalid.");
public sealed class TaskVersionConflictException(long actual, long expected) : Exception($"Expected version {expected}, current version is {actual}.") { public long ActualVersion { get; } = actual; public long ExpectedVersion { get; } = expected; }

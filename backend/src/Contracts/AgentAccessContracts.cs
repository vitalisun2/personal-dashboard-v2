using PersonalDashboard.V2.Contracts.Planning;

namespace PersonalDashboard.V2.Contracts.AgentAccess;

// Every member affected by a reorder is checked again at confirmation time.
public sealed record VersionedEntityId(Guid Id, long ExpectedVersion);

public enum KnowledgeNodeKind { Section, Document }
public enum KnowledgeMutationKind { Create, Update, Move, Archive, Restore, Delete, Reorder }

public sealed record KnowledgeNodeState(
    KnowledgeNodeKind Kind,
    Guid Id,
    Guid? ParentSectionId,
    long Version,
    string Title,
    string? Markdown,
    string Path,
    bool Archived,
    int Position);

public sealed record KnowledgeMutation(
    KnowledgeMutationKind Operation,
    KnowledgeNodeKind Kind,
    Guid Id,
    long? ExpectedVersion,
    Guid? ParentSectionId = null,
    string? Title = null,
    string? Markdown = null,
    IReadOnlyList<VersionedEntityId>? Order = null);

public sealed record KnowledgeMutationResult(bool Applied, KnowledgeNodeState? Current, string? ConflictReason);

public interface IKnowledgeAgentAccess
{
    Task<IReadOnlyList<KnowledgeNodeState>> ListSectionsAsync(CancellationToken cancellationToken = default);
    Task<KnowledgeNodeState?> ReadAsync(
        KnowledgeNodeKind kind,
        Guid id,
        CancellationToken cancellationToken = default);

    Task<KnowledgeMutationResult> ApplyAsync(
        KnowledgeMutation mutation,
        CancellationToken cancellationToken = default);
}

public enum PlanningEntityKind { Project, Milestone, Feature }
public enum PlanningMutationKind { Create, Update, Archive, Restore, Delete, SetFeatureStatus, Reorder, Replace }

public sealed record PlanningEntityState(
    PlanningEntityKind Kind,
    Guid Id,
    Guid? ProjectId,
    Guid? MilestoneId,
    long Version,
    string Title,
    string? Description,
    string? FeatureStatus,
    bool Archived,
    int Position);

public sealed record PlanningMutation(
    PlanningMutationKind Operation,
    PlanningEntityKind Kind,
    Guid Id,
    Guid? ProjectId,
    Guid? MilestoneId,
    long? ExpectedVersion,
    string? Title = null,
    string? Description = null,
    string? FeatureStatus = null,
    IReadOnlyList<VersionedEntityId>? Order = null,
    long? ExpectedParentVersion = null,
    bool? IsArchived = null);

public sealed record PlanningMutationResult(bool Applied, PlanningEntityState? Current, string? ConflictReason);

public interface IPlanningAgentAccess
{
    Task<IReadOnlyList<TaskFeatureTarget>> ListTaskFeaturesAsync(CancellationToken cancellationToken = default);
    Task<PlanningEntityState?> ReadAsync(
        PlanningEntityKind kind,
        Guid id,
        CancellationToken cancellationToken = default);

    Task<PlanningMutationResult> ApplyAsync(
        PlanningMutation mutation,
        CancellationToken cancellationToken = default);
}

public sealed record TaskFeatureTarget(
    Guid ProjectId, string ProjectTitle, long ProjectVersion,
    Guid MilestoneId, string MilestoneTitle, long MilestoneVersion,
    Guid FeatureId, string FeatureTitle, long FeatureVersion,
    string Path);

public enum TaskEntityKind { Task, Section }
public enum TaskMutationKind { Create, Update, Move, SetWorkStatus, Archive, Restore, Delete, Reorder, Replace }

public sealed record TaskEntityState(
    TaskEntityKind Kind,
    Guid Id,
    long Version,
    string Title,
    string? Description,
    PlanningLink? Planning,
    string? Placement,
    string? WorkStatus,
    Guid? SectionId,
    string? Bucket,
    int Position,
    string? ArchivedSectionName = null,
    int PlanningPosition = 0);

public sealed record TaskMutation(
    TaskMutationKind Operation,
    TaskEntityKind Kind,
    Guid Id,
    long? ExpectedVersion,
    string? Title = null,
    string? Description = null,
    PlanningLink? Planning = null,
    string? Placement = null,
    string? WorkStatus = null,
    Guid? SectionId = null,
    string? Bucket = null,
    IReadOnlyList<VersionedEntityId>? Order = null,
    int? Position = null,
    string? ArchivedSectionName = null,
    bool PreserveSectionWhenEmpty = false);

public sealed record TaskMutationResult(bool Applied, TaskEntityState? Current, string? ConflictReason);

public interface ITasksAgentAccess
{
    Task<IReadOnlyList<TaskBacklogSection>> ListBacklogSectionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ListPlanningTaskIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
    Task<TaskEntityState?> ReadAsync(
        TaskEntityKind kind,
        Guid id,
        CancellationToken cancellationToken = default);

    Task<TaskMutationResult> ApplyAsync(
        TaskMutation mutation,
        CancellationToken cancellationToken = default);
}

public sealed record TaskBacklogSection(Guid Id, string Name, long Version);

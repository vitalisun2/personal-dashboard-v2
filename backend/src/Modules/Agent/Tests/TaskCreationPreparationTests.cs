using System.Text.Json;
using PersonalDashboard.V2.Agent.Application;
using PersonalDashboard.V2.Agent.Domain;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;
using PersonalDashboard.V2.Tasks.Infrastructure;
using Xunit;

namespace PersonalDashboard.V2.Agent.Tests;

public sealed class TaskCreationPreparationTests
{
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MilestoneId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid FeatureId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid SectionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task Feature_task_routes_to_existing_planned_chain_with_empty_body_and_preview()
    {
        var tool = $$"""{"title":"Проверить вход","description":"","description_quote":null,"destination":"planned","section":null,"section_quote":null,"feature":"Продукт / Безопасность / Авторизация","feature_quote":"в фичу авторизации","project":null,"milestone":null,"destination_quote":null,"reason":"Пользователь указал существующую фичу."}""";
        var service = CreateService(new Queue<ModelCompletion>([
            new ModelCompletion(null, [new ModelToolCall("catalog", "list_task_destinations", "{}")]),
            new ModelCompletion(null, [new ModelToolCall("prepare", "prepare_task", tool)])
        ]));

        var result = await service.RespondAsync(Request("Добавь задачу «Проверить вход» в фичу авторизации."));

        Assert.True(result.Proposal is not null, result.Answer);
        var proposal = Assert.Single(result.Proposal!.Changes);
        Assert.Equal("tasks.task", proposal.Target.EntityType);
        using var payload = JsonDocument.Parse(proposal.AfterJson);
        Assert.Equal("Проверить вход", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal("", payload.RootElement.GetProperty("description").GetString());
        Assert.Equal("planned", payload.RootElement.GetProperty("placement").GetString());
        Assert.Equal(FeatureId, payload.RootElement.GetProperty("planning").GetProperty("featureId").GetGuid());
        Assert.Contains("План · Продукт / Безопасность / Авторизация", proposal.Preview);
        Assert.Contains("без описания", proposal.Preview);
    }

    [Fact]
    public async Task Repeating_identical_pending_task_does_not_create_a_second_proposal()
    {
        var original = new
        {
            title = "Проверить вход", description = "проверить журнал ошибок", placement = "planned",
            sectionId = (Guid?)null,
            planning = new { projectId = ProjectId, milestoneId = MilestoneId, featureId = FeatureId },
            backlogSectionName = (string?)null, expectedBacklogSectionVersion = (long?)null,
            featurePath = "Продукт / Безопасность / Авторизация", expectedProjectVersion = 4L,
            expectedMilestoneVersion = 3L, expectedFeatureVersion = 2L,
            taskPlacementReason = "Связана с указанной фичей и будет добавлена в план."
        };
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(original)).RootElement.Clone();
        var pending = new ChatProposal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            [new ChatProposedAction(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null, payload,
                "Проверить вход", null, payload)], ChatProposalState.Pending, null, DateTimeOffset.UtcNow);
        var tool = JsonSerializer.Serialize(new
        {
            title = original.title, description = original.description, destination = "planned", section = (string?)null,
            feature = original.featurePath, feature_quote = (string?)null, destination_quote = (string?)null
        });
        var service = CreateService(new Queue<ModelCompletion>([
            new ModelCompletion(null, [new ModelToolCall("catalog", "list_task_destinations", "{}")]),
            new ModelCompletion(null, [new ModelToolCall("prepare", "prepare_task", tool)])
        ]));

        var result = await service.RespondAsync(Request("Оставь как есть.", pending));

        Assert.Null(result.Proposal);
        Assert.Contains("уже подготовлено", result.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_description_argument_is_rejected_but_empty_description_is_valid()
    {
        var tool = """{"title":"Позвонить","destination":"backlog","section":"Общее","feature":null,"feature_quote":null,"destination_quote":null}""";
        var service = CreateService(new Queue<ModelCompletion>([
            new ModelCompletion(null, [new ModelToolCall("catalog", "list_task_destinations", "{}")]),
            new ModelCompletion(null, [new ModelToolCall("prepare", "prepare_task", tool)])
        ]));

        var result = await service.RespondAsync(Request("Добавь задачу «Позвонить»."));

        Assert.Null(result.Proposal);
        Assert.Contains("поле description", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("пустую строку", result.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pending_task_rename_without_description_preserves_existing_description()
    {
        var original = new
        {
            title = "Проверить вход", description = "проверить журнал ошибок", placement = "planned",
            sectionId = (Guid?)null,
            planning = new { projectId = ProjectId, milestoneId = MilestoneId, featureId = FeatureId },
            backlogSectionName = (string?)null, expectedBacklogSectionVersion = (long?)null,
            featurePath = "Продукт / Безопасность / Авторизация", expectedProjectVersion = 4L,
            expectedMilestoneVersion = 3L, expectedFeatureVersion = 2L,
            taskPlacementReason = "Связана с указанной фичей и будет добавлена в план."
        };
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(original)).RootElement.Clone();
        var pending = new ChatProposal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            [new ChatProposedAction(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null, payload,
                original.title, null, payload)], ChatProposalState.Pending, null, DateTimeOffset.UtcNow);
        var tool = """{"title":"Проверить авторизацию","destination":"planned","section":null,"feature":"Продукт / Безопасность / Авторизация","feature_quote":null,"destination_quote":null}""";
        var service = CreateService(new Queue<ModelCompletion>([
            new ModelCompletion(null, [new ModelToolCall("catalog", "list_task_destinations", "{}")]),
            new ModelCompletion(null, [new ModelToolCall("prepare", "prepare_task", tool)])
        ]));

        var result = await service.RespondAsync(Request("Переименуй в «Проверить авторизацию».", pending));

        Assert.True(result.Proposal is not null, result.Answer);
        using var revised = JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
        Assert.Equal("Проверить авторизацию", revised.RootElement.GetProperty("title").GetString());
        Assert.Equal(original.description, revised.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Pending_task_empty_description_explicitly_clears_existing_description()
    {
        var original = new
        {
            title = "Проверить вход", description = "проверить журнал ошибок", placement = "planned",
            sectionId = (Guid?)null,
            planning = new { projectId = ProjectId, milestoneId = MilestoneId, featureId = FeatureId },
            backlogSectionName = (string?)null, expectedBacklogSectionVersion = (long?)null,
            featurePath = "Продукт / Безопасность / Авторизация", expectedProjectVersion = 4L,
            expectedMilestoneVersion = 3L, expectedFeatureVersion = 2L,
            taskPlacementReason = "Связана с указанной фичей и будет добавлена в план."
        };
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(original)).RootElement.Clone();
        var pending = new ChatProposal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            [new ChatProposedAction(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null, payload,
                original.title, null, payload)], ChatProposalState.Pending, null, DateTimeOffset.UtcNow);
        var tool = """{"title":"Проверить вход","description":"","destination":"planned","section":null,"feature":"Продукт / Безопасность / Авторизация","feature_quote":null,"destination_quote":null}""";
        var service = CreateService(new Queue<ModelCompletion>([
            new ModelCompletion(null, [new ModelToolCall("catalog", "list_task_destinations", "{}")]),
            new ModelCompletion(null, [new ModelToolCall("prepare", "prepare_task", tool)])
        ]));

        var result = await service.RespondAsync(Request("Убери описание.", pending));

        Assert.True(result.Proposal is not null, result.Answer);
        using var revised = JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
        Assert.Equal("", revised.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Explicit_project_without_feature_is_clarified_instead_of_becoming_backlog_task()
    {
        var tool = """{"title":"Проверить доступ","description":"","description_quote":null,"destination":"backlog","section":"Общее","section_quote":null,"feature":null,"feature_quote":null,"project":"Продукт","milestone":null,"destination_quote":null,"reason":""}""";
        var service = CreateService(new Queue<ModelCompletion>([
            new ModelCompletion(null, [new ModelToolCall("catalog", "list_task_destinations", "{}")]),
            new ModelCompletion(null, [new ModelToolCall("prepare", "prepare_task", tool)])
        ]));

        var result = await service.RespondAsync(Request("Добавь задачу «Проверить доступ» в проект Продукт."));

        Assert.Null(result.Proposal);
        Assert.Contains("этап", result.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Неизвестный путь", "в фичу Неизвестный путь", "не найдена")]
    [InlineData("Продукт / Безопасность / Авторизация", "в фичу авторизации", "неоднознач")]
    public async Task Missing_or_ambiguous_feature_is_not_silently_routed(string feature, string quote, string expected)
    {
        var tool = JsonSerializer.Serialize(new
        {
            title = "Проверить вход", description = "", destination = "planned", section = (string?)null,
            feature, feature_quote = quote, destination_quote = (string?)null
        });
        var features = expected == "неоднознач"
            ? new[]
            {
                new TaskFeatureTarget(ProjectId, "Продукт", 4, MilestoneId, "Безопасность", 3, FeatureId, "Авторизация", 2, "Продукт / Безопасность / Авторизация"),
                new TaskFeatureTarget(Guid.NewGuid(), "Личное", 1, Guid.NewGuid(), "Доступ", 1, Guid.NewGuid(), "Авторизация", 1, "Личное / Доступ / Авторизация")
            }
            : [new TaskFeatureTarget(ProjectId, "Продукт", 4, MilestoneId, "Безопасность", 3, FeatureId, "Авторизация", 2, "Продукт / Безопасность / Авторизация")];
        var service = CreateService(new Queue<ModelCompletion>([
            new ModelCompletion(null, [new ModelToolCall("catalog", "list_task_destinations", "{}")]),
            new ModelCompletion(null, [new ModelToolCall("prepare", "prepare_task", tool)])
        ]), features);

        var result = await service.RespondAsync(Request("Добавь задачу в " + quote + "."));

        Assert.Null(result.Proposal);
        Assert.Contains(expected, result.Answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Real_tasks_service_keeps_feature_chain_for_planned_and_linked_backlog_tasks()
    {
        var repository = new MemoryTasksRepository();
        var service = new TasksService(repository, new ValidPlanningLinks(), new ImmediateTransactionRunner());
        var access = new TasksAgentAccess(service);
        var plannedId = Guid.NewGuid();
        var backlogId = Guid.NewGuid();

        var planned = await access.ApplyAsync(new TaskMutation(TaskMutationKind.Create, TaskEntityKind.Task, plannedId, null,
            "Проверить вход", "", new PlanningLink(ProjectId, MilestoneId, FeatureId), "planned"));
        var backlog = await access.ApplyAsync(new TaskMutation(TaskMutationKind.Create, TaskEntityKind.Task, backlogId, null,
            "Проверить доступ", "", new PlanningLink(ProjectId, MilestoneId, FeatureId), "backlog"));

        Assert.True(planned.Applied, planned.ConflictReason);
        Assert.Equal("planned", planned.Current!.Placement);
        Assert.Equal(new PlanningLink(ProjectId, MilestoneId, FeatureId), planned.Current.Planning);
        Assert.True(backlog.Applied, backlog.ConflictReason);
        Assert.Equal("backlog", backlog.Current!.Placement);
        Assert.Equal(new PlanningLink(ProjectId, MilestoneId, FeatureId), backlog.Current.Planning);
        Assert.Null(backlog.Current.SectionId);
    }

    private static AgentTurnService CreateService(Queue<ModelCompletion> calls, IReadOnlyList<TaskFeatureTarget>? features = null) => new(
        new QueueRouter(calls), new EmptySearch(), new EmptyKnowledge(), new CatalogPlanning(features), new CatalogTasks());

    private static AgentTurnRequest Request(string prompt, ChatProposal? pending = null) => new(Guid.NewGuid(), Guid.NewGuid(), prompt,
        new AgentScope("general", null, null, null), "Gemma", ChatModelRoute.Default,
        pending is null ? [] : [ProposalDraftContext.ToMessage(pending)], PendingProposal: pending);

    private sealed class QueueRouter(Queue<ModelCompletion> calls) : IChatModelRouter
    {
        public Task<RoutedCompletion> CompleteAsync(string requestedModel, ModelCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new RoutedCompletion(calls.Count > 0 ? calls.Dequeue() : new ModelCompletion(request.Messages[^1].Content, []), requestedModel, requestedModel, null));
    }
    private sealed class EmptySearch : ISearchService
    {
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResponse([], null, true, null));
    }
    private sealed class EmptyKnowledge : IKnowledgeAgentAccess
    {
        public Task<IReadOnlyList<KnowledgeNodeState>> ListSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KnowledgeNodeState>>([]);
        public Task<KnowledgeNodeState?> ReadAsync(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<KnowledgeNodeState?>(null);
        public Task<KnowledgeMutationResult> ApplyAsync(KnowledgeMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
    private sealed class EmptyPlanning : IPlanningAgentAccess
    {
        public Task<IReadOnlyList<TaskFeatureTarget>> ListTaskFeaturesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskFeatureTarget>>([]);
        public Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<PlanningEntityState?>(null);
        public Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
    private sealed class CatalogPlanning(IReadOnlyList<TaskFeatureTarget>? features = null) : IPlanningAgentAccess
    {
        private IReadOnlyList<TaskFeatureTarget> Features { get; } = features ?? [new(ProjectId, "Продукт", 4, MilestoneId, "Безопасность", 3, FeatureId, "Авторизация", 2, "Продукт / Безопасность / Авторизация")];
        public Task<IReadOnlyList<TaskFeatureTarget>> ListTaskFeaturesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Features);
        public Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<PlanningEntityState?>(null);
        public Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
    private sealed class CatalogTasks : ITasksAgentAccess
    {
        private static readonly IReadOnlyList<TaskBacklogSection> Sections = [new(SectionId, "Общее", 1)];
        public Task<IReadOnlyList<TaskBacklogSection>> ListBacklogSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Sections);
        public Task<TaskEntityState?> ReadAsync(TaskEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskEntityState?>(null);
        public Task<TaskMutationResult> ApplyAsync(TaskMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class ValidPlanningLinks : IPlanningLinkValidator
    {
        public Task<PlanningLinkValidationResult> ValidateAsync(PlanningLink link, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlanningLinkValidationResult(true, []));
    }
    private sealed class ImmediateTransactionRunner : ITransactionRunner
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class MemoryTasksRepository : ITasksRepository
    {
        private readonly Dictionary<Guid, TaskItem> _items = [];
        private readonly Dictionary<Guid, TaskSection> _sections = new() { [SectionId] = new TaskSection("Общее", TaskLocation.Backlog, SectionId) };
        public Task<IReadOnlyList<TaskItem>> ListAsync(TaskLocation? location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskItem>>(_items.Values.Where(item => location is null || item.Location == location).ToArray());
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(_items.GetValueOrDefault(id));
        public Task SaveAsync(TaskItem item, CancellationToken ct) { _items[item.Id] = item; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken ct) { _items.Remove(id); return Task.CompletedTask; }
        public Task<IReadOnlyList<TaskSection>> ListSectionsAsync(TaskLocation location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskSection>>(_sections.Values.Where(section => section.Location == location).ToArray());
        public Task SaveSectionAsync(TaskSection section, CancellationToken ct) { _sections[section.Id] = section; return Task.CompletedTask; }
        public Task DeleteSectionAsync(Guid id, CancellationToken ct) { _sections.Remove(id); return Task.CompletedTask; }
    }
}

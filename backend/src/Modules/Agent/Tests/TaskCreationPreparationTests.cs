using System.Text.Json;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalDashboard.V2.Agent.Api;
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
    public async Task Progress_callback_reports_actual_model_catalog_and_proposal_boundaries_in_order()
    {
        var tool = """{"title":"Позвонить","description":"","destination":"backlog","section":"Общее","feature":null,"feature_quote":null,"destination_quote":null}""";
        var service = CreateService(new Queue<ModelCompletion>([
            new ModelCompletion(null, [new ModelToolCall("catalog", "list_task_destinations", "{}")]),
            new ModelCompletion(null, [new ModelToolCall("prepare", "prepare_task", tool)])
        ]));
        var progress = new List<(string Stage, string Text)>();

        var result = await service.RespondAsync(Request("Добавь задачу «Позвонить».", progress: (stage, text, _) =>
        {
            progress.Add((stage, text));
            return ValueTask.CompletedTask;
        }));

        Assert.NotNull(result.Proposal);
        Assert.Equal(["processing", "catalog", "reasoning", "preparing"], progress.Select(item => item.Stage));
        Assert.Contains("Обрабатываю", progress[0].Text);
        Assert.Contains("фичи", progress[1].Text);
        Assert.DoesNotContain(progress, item => item.Text.Contains("prepare_task", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ndjson_result_uses_web_json_names_for_nested_search_highlight()
    {
        var range = new SearchTextRange(4, 9);
        var turn = new ChatTurn(Guid.NewGuid(), Guid.NewGuid(), "найди", "нашёл", new("general", null, null, null),
            "Gemma", "Gemma", ChatModelRoute.Default,
            [new SearchSourceReference("knowledge", Guid.NewGuid(), 1, "https://example.test", "Источник", null,
                "Текст", DateTimeOffset.UtcNow, false, null, range)], DateTimeOffset.UtcNow);
        var toTurnView = typeof(AgentApiModule).GetMethod("ToTurnView", BindingFlags.NonPublic | BindingFlags.Static)!;
        var view = toTurnView.Invoke(null, [turn, null])!;
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var write = typeof(AgentApiModule).GetMethod("WriteNdjsonAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)write.Invoke(null, [context, new { type = "result", turn = view },
            new JsonSerializerOptions(JsonSerializerDefaults.Web), CancellationToken.None])!;
        await task;

        body.Position = 0;
        using var line = await JsonDocument.ParseAsync(body);
        var highlight = line.RootElement.GetProperty("turn").GetProperty("sourceDetails")[0].GetProperty("highlight");
        Assert.Equal(4, highlight.GetProperty("start").GetInt32());
        Assert.Equal(9, highlight.GetProperty("length").GetInt32());
        Assert.EndsWith("\n", System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task Send_turn_keeps_json_default_and_streams_errors_only_after_progress_starts()
    {
        var conversationId = Guid.NewGuid();
        var unavailableBeforeProgress = await InvokeSendTurnAsync(conversationId, "application/x-ndjson",
            new ThrowingTurnService((_, _) => throw ModelUnavailable()), CancellationToken.None);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailableBeforeProgress.Context.Response.StatusCode);
        Assert.Contains("application/json", unavailableBeforeProgress.Context.Response.ContentType);

        var unavailableAfterProgress = await InvokeSendTurnAsync(conversationId, "application/x-ndjson",
            new ThrowingTurnService(async (request, token) =>
            {
                await request.Progress!("processing", "Обрабатываю сообщение…", token);
                throw ModelUnavailable();
            }), CancellationToken.None);
        Assert.Equal("application/x-ndjson; charset=utf-8", unavailableAfterProgress.Context.Response.ContentType);
        var streamed = await ReadLinesAsync(unavailableAfterProgress.Context.Response.Body);
        Assert.Equal(["progress", "error"], streamed.Select(line => line.RootElement.GetProperty("type").GetString()));

        var normalJson = await InvokeSendTurnAsync(conversationId, "application/json",
            new ThrowingTurnService((request, _) => Task.FromResult(new AgentTurnResult("Ответ", request.Scope,
                "Gemma", "Gemma", ChatModelRoute.Default, null, null, []))), CancellationToken.None);
        Assert.Contains("application/json", normalJson.Context.Response.ContentType);
        Assert.Single(normalJson.Store.Turns);
        Assert.DoesNotContain("\"type\":\"result\"", await ReadBodyAsync(normalJson.Context.Response.Body));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeSendTurnAsync(conversationId,
            "application/x-ndjson", new ThrowingTurnService((_, _) => throw new OperationCanceledException()), cancellation.Token));
    }

    private static async Task<(DefaultHttpContext Context, TestChatStore Store)> InvokeSendTurnAsync(Guid conversationId,
        string accept, IAgentTurnService agent, CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = accept;
        context.RequestAborted = cancellationToken;
        context.Response.Body = new MemoryStream();
        var services = new ServiceCollection().AddLogging().ConfigureHttpJsonOptions(_ => { }).BuildServiceProvider();
        context.RequestServices = services;
        var store = new TestChatStore();
        var method = typeof(AgentApiModule).GetMethod("SendTurnAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var requestType = typeof(AgentApiModule).GetNestedType("SendTurnRequest", BindingFlags.NonPublic)!;
        var request = Activator.CreateInstance(requestType, "Проверь статус", null, null)!;
        var task = (Task<IResult>)method.Invoke(null, [context, conversationId, request, store, agent,
            new ImmediateTransactionRunner(), new EmptyIndexer(), services.GetRequiredService<ILoggerFactory>(),
            services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>(), cancellationToken])!;
        var result = await task;
        await result.ExecuteAsync(context);
        return (context, store);
    }

    private static ChatModelUnavailableException ModelUnavailable() => new("offline", new InvalidOperationException());

    private static async Task<List<JsonDocument>> ReadLinesAsync(Stream stream)
    {
        var body = await ReadBodyAsync(stream);
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToList();
    }

    private static async Task<string> ReadBodyAsync(Stream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return await reader.ReadToEndAsync();
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
        Assert.Contains("эпик", result.Answer, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task Existing_backlog_task_can_be_linked_and_moved_to_planning_in_one_synced_update()
    {
        var repository = new MemoryTasksRepository();
        var access = new TasksAgentAccess(new TasksService(repository, new ValidPlanningLinks(), new ImmediateTransactionRunner()));
        var id = Guid.NewGuid();
        var created = await access.ApplyAsync(new TaskMutation(TaskMutationKind.Create, TaskEntityKind.Task, id, null, "Задача", "Описание", Placement: "backlog"));

        var moved = await access.ApplyAsync(new TaskMutation(TaskMutationKind.Update, TaskEntityKind.Task, id, created.Current!.Version,
            Planning: new PlanningLink(ProjectId, MilestoneId, FeatureId), Placement: "planned"));

        Assert.True(moved.Applied, moved.ConflictReason);
        Assert.Equal(id, moved.Current!.Id);
        Assert.Equal("Задача", moved.Current.Title);
        Assert.Equal("Описание", moved.Current.Description);
        Assert.Equal("planned", moved.Current.Placement);
        Assert.Equal(new PlanningLink(ProjectId, MilestoneId, FeatureId), moved.Current.Planning);
        Assert.Null(moved.Current.SectionId);
    }

    [Fact]
    public async Task Invalid_planning_link_does_not_partially_update_task_or_advance_its_version()
    {
        var repository = new MemoryTasksRepository();
        var id = Guid.NewGuid();
        var original = new TaskItem("Исходное название", "Исходное описание", id: id);
        await repository.SaveAsync(original, CancellationToken.None);
        var access = new TasksAgentAccess(new TasksService(repository, new InvalidPlanningLinks(), new ImmediateTransactionRunner()));

        var result = await access.ApplyAsync(new TaskMutation(TaskMutationKind.Update, TaskEntityKind.Task, id, original.Version,
            Title: "Изменённое название", Description: "Изменённое описание", Planning: new PlanningLink(ProjectId, MilestoneId, FeatureId)));

        Assert.False(result.Applied);
        Assert.Equal(original.Version, result.Current!.Version);
        Assert.Equal("Исходное название", result.Current.Title);
        Assert.Equal("Исходное описание", result.Current.Description);
        Assert.Null(result.Current.Planning);
    }

    [Fact]
    public async Task Planned_task_can_be_reassigned_to_another_feature_without_leaving_planning()
    {
        var repository = new MemoryTasksRepository();
        var access = new TasksAgentAccess(new TasksService(repository, new ValidPlanningLinks(), new ImmediateTransactionRunner()));
        var id = Guid.NewGuid();
        var created = await access.ApplyAsync(new TaskMutation(TaskMutationKind.Create, TaskEntityKind.Task, id, null,
            "Запланированная задача", "Описание", new PlanningLink(ProjectId, MilestoneId, FeatureId), "planned"));
        var replacement = new PlanningLink(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var updated = await access.ApplyAsync(new TaskMutation(TaskMutationKind.Update, TaskEntityKind.Task, id, created.Current!.Version,
            Planning: replacement, Placement: "planned"));

        Assert.True(updated.Applied, updated.ConflictReason);
        Assert.Equal("planned", updated.Current!.Placement);
        Assert.Equal(replacement, updated.Current.Planning);
        Assert.Equal("Запланированная задача", updated.Current.Title);
    }

    private static AgentTurnService CreateService(Queue<ModelCompletion> calls, IReadOnlyList<TaskFeatureTarget>? features = null) => new(
        new QueueRouter(calls), new EmptySearch(), new EmptyKnowledge(), new CatalogPlanning(features), new CatalogTasks());

    private static AgentTurnRequest Request(string prompt, ChatProposal? pending = null, Func<string, string, CancellationToken, ValueTask>? progress = null) => new(Guid.NewGuid(), Guid.NewGuid(), prompt,
        new AgentScope("general", null, null, null), "Gemma", ChatModelRoute.Default,
        pending is null ? [] : [ProposalDraftContext.ToMessage(pending)], PendingProposal: pending, Progress: progress);

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
    private sealed class InvalidPlanningLinks : IPlanningLinkValidator
    {
        public Task<PlanningLinkValidationResult> ValidateAsync(PlanningLink link, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlanningLinkValidationResult(false, []));
    }
    private sealed class ImmediateTransactionRunner : ITransactionRunner
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class ThrowingTurnService(Func<AgentTurnRequest, CancellationToken, Task<AgentTurnResult>> respond) : IAgentTurnService
    {
        public Task<AgentTurnResult> RespondAsync(AgentTurnRequest request, CancellationToken cancellationToken = default) => respond(request, cancellationToken);
    }
    private sealed class EmptyIndexer : ISearchIndexer
    {
        public Task UpsertAsync(SearchIndexSource source, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string kind, Guid id, long version, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class TestChatStore : IChatConversationStore
    {
        public List<ChatTurn> Turns { get; } = [];
        public Task<ChatConversationState?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ChatConversationState?>(new(id, "Test", DateTimeOffset.UtcNow));
        public Task<ChatConversationState> CreateConversationAsync(Guid id, string? title, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChatConversationPage> ListConversationsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteConversationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatTurn>> GetRecentTurnsAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChatTurn>>([]);
        public Task<ChatTurnPage> GetTurnsPageAsync(Guid conversationId, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AppendTurnAsync(ChatTurn turn, CancellationToken cancellationToken = default) { Turns.Add(turn); return Task.CompletedTask; }
        public Task<ChatProposal?> GetProposalAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ChatProposal?>(null);
        public Task<ChatProposal?> GetProposalForTurnAsync(Guid turnId, CancellationToken cancellationToken = default) => Task.FromResult<ChatProposal?>(null);
        public Task<ChatProposal?> GetPendingProposalAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<ChatProposal?>(null);
        public Task SaveProposalAsync(ChatProposal proposal, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> DismissPendingProposalsAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> TryChangeProposalStateAsync(Guid id, ChatProposalState expectedState, ChatProposalState newState, Guid? confirmationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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

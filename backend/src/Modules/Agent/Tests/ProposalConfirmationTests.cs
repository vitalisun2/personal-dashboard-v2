using System.Text.Json;
using PersonalDashboard.V2.Agent.Application;
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

public sealed class ProposalConfirmationTests
{
    [Fact]
    public void Task_proposal_context_does_not_direct_the_model_to_create_a_document()
    {
        var payload = JsonDocument.Parse("""{"title":"Позвонить"}""").RootElement.Clone();
        var draft = Proposal([new ChatProposedAction(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null,
            payload, "Позвонить", null, payload)]);
        var context = ProposalDraftContext.ToMessage(draft).Content;
        Assert.Contains("Позвонить", context);
        Assert.Contains("кнопку добавления", context);
        Assert.DoesNotContain("prepare_knowledge_document", context);
        Assert.DoesNotContain("Создать документ", context);
    }

    [Theory]
    [InlineData("Идеи", false)]
    [InlineData("Новые идеи", true)]
    public async Task Pending_document_reuses_location_and_only_replaces_changed_content(string title, bool changed)
    {
        var payload = JsonDocument.Parse("""{"title":"Идеи","markdown":"Текст","placementKind":"root_explicit"}""").RootElement.Clone();
        var draft = Proposal([new ChatProposedAction(Guid.NewGuid(), "knowledge.document", Guid.NewGuid(), "Create", null,
            payload, "Идеи", null, payload)]);
        var calls = new Queue<ModelCompletion>([
            PrepareDocumentCall($$"""{"title":"{{title}}","markdown":"Текст","section":"корень","section_quote":null}""")
        ]);
        var result = await CreateDocumentService(calls, []).RespondAsync(Request(changed ? "Назови иначе" : "Да, создавай")
            with { PendingProposal = draft });
        Assert.Equal(changed, result.Proposal is not null);
        if (!changed) Assert.Contains("кнопку", result.Answer);
        else Assert.Contains("Текст", Assert.Single(result.Proposal!.Changes).AfterJson);
    }

    [Fact]
    public async Task Model_supplied_display_label_and_preview_are_not_authoritative()
    {
        var toolArgs = JsonSerializer.Serialize(new
        {
            changes = new[]
            {
                new { module = "Knowledge", operation = "Create", entityType = "knowledge.document",
                    displayName = "Delete everything", preview = "Safe to confirm",
                    after = new { title = "New title", markdown = "new body", placement_kind = "root_explicit" } }
            }
        });
        var service = new AgentTurnService(new FixedRouter(toolArgs), new EmptySearch(), new KnowledgeAccess([]), new EmptyPlanning(), new EmptyTasks());
        var response = await service.RespondAsync(new AgentTurnRequest(Guid.NewGuid(), Guid.NewGuid(), "Please edit the doc.",
            new AgentScope("general", null, null, null), "Gemma", PersonalDashboard.V2.Contracts.Chat.ChatModelRoute.Default, []));

        var change = Assert.Single(Assert.IsType<PersonalDashboard.V2.Agent.Domain.ChangeProposal>(response.Proposal).Changes);
        Assert.Equal("Документ знаний · New title", change.DisplayName);
        Assert.DoesNotContain("Delete everything", change.DisplayName);
        Assert.DoesNotContain("Safe to confirm", change.Preview);
        Assert.Contains("Название: New title", change.Preview);
        Assert.Contains("new body", change.Preview);
    }

    [Fact]
    public async Task Router_uses_only_Gemma_and_does_not_fallback_on_invalid_response()
    {
        var gemma = new Provider("Gemma", new ModelCompletion(null, [new ModelToolCall("1", "search_app", "not-json")]));
        var request = new ModelCompletionRequest([], [new ModelTool("search_app", "search", "{}")]);
        var router = new ChatModelRouter([gemma]);
        var unavailable = await Assert.ThrowsAsync<ChatModelUnavailableException>(() => router.CompleteAsync("Gemma", request, CancellationToken.None));
        Assert.IsAssignableFrom<JsonException>(unavailable.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.CompleteAsync("DeepSeek", request, CancellationToken.None));
        Assert.Equal(1, gemma.Calls);
    }

    [Fact]
    public async Task Confirmation_rejects_multi_action_packages_and_same_confirmation_is_idempotent()
    {
        var store = new Store();
        var transactions = new RollbackRunner();
        var knowledge = new KnowledgeAccess(transactions.Writes);
        var service = new ProposalConfirmationService(store, transactions, knowledge, new EmptyPlanning(), new EmptyTasks());

        store.Proposal = Proposal([Action("first"), Action("second")]);
        var rejected = await service.ConfirmAsync(store.Proposal.Id, Guid.NewGuid());
        Assert.False(rejected.Applied);
        Assert.Empty(transactions.Writes);

        store.Proposal = Proposal([Action("safe")]);
        var confirmationId = Guid.NewGuid();
        var first = await service.ConfirmAsync(store.Proposal.Id, confirmationId);
        var repeat = await service.ConfirmAsync(store.Proposal.Id, confirmationId);
        Assert.True(first.Applied);
        Assert.True(repeat.AlreadyApplied);
        Assert.Single(transactions.Writes);
    }

    [Fact]
    public async Task Root_inferred_document_preview_has_empty_body_and_deterministic_reason()
    {
        var calls = new Queue<ModelCompletion>([
            new(null, [new ModelToolCall("sections", "list_knowledge_sections", "{}")]),
            ProposalCall("""{"title":"Рецепт пирога","markdown":"","placement_kind":"root_inferred","placement_reason":"среди разделов нет кулинарии"}""")
        ]);
        var service = CreateDocumentService(calls, []);

        var result = await service.RespondAsync(Request("Добавь документ «Рецепт пирога»."));

        Assert.True(result.Proposal is not null, result.Answer);
        var change = Assert.Single(result.Proposal!.Changes);
        using var payload = JsonDocument.Parse(change.AfterJson);
        Assert.Equal("", payload.RootElement.GetProperty("markdown").GetString());
        Assert.Equal("root_inferred", payload.RootElement.GetProperty("placementKind").GetString());
        Assert.False(payload.RootElement.TryGetProperty("parentSectionId", out _));
        Assert.Contains("ни один раздел не подошёл", change.Preview);
        Assert.Contains("Описание: не было дано", change.Preview);
    }

    [Fact]
    public async Task Inferred_section_uses_catalog_ID_and_full_path_in_preview()
    {
        var sectionId = Guid.NewGuid();
        var section = new KnowledgeNodeState(KnowledgeNodeKind.Section, sectionId, null, 4, "Поездки", null, "Личное / Поездки", false, 0);
        var calls = new Queue<ModelCompletion>([
            new(null, [new ModelToolCall("sections", "list_knowledge_sections", "{}")]),
            ProposalCall($$"""{"title":"Поездка в Казань","markdown":"проверить билеты","placement_kind":"inferred_section","parent_section_id":"{{sectionId:D}}","placement_reason":"относится к планированию поездки"}""")
        ]);

        var result = await CreateDocumentService(calls, [section]).RespondAsync(Request("Добавь документ «Поездка в Казань» и запиши проверить билеты."));

        Assert.True(result.Proposal is not null, result.Answer);
        var change = Assert.Single(result.Proposal!.Changes);
        using var payload = JsonDocument.Parse(change.AfterJson);
        Assert.Equal(sectionId, payload.RootElement.GetProperty("parentSectionId").GetGuid());
        Assert.Equal(4, payload.RootElement.GetProperty("expectedParentVersion").GetInt64());
        Assert.Equal("Личное / Поездки", payload.RootElement.GetProperty("parentSectionPath").GetString());
        Assert.Contains("Раздел не был указан. Предлагаю «Личное / Поездки»", change.Preview);
        Assert.Contains("относится к планированию поездки", change.Preview);
        Assert.Equal("проверить билеты", payload.RootElement.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task Explicit_duplicate_section_name_is_rejected_even_if_model_selects_one_ID()
    {
        var first = new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 1, "Заметки", null, "Дом / Заметки", false, 0);
        var second = new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 1, "Заметки", null, "Работа / Заметки", false, 1);
        var calls = new Queue<ModelCompletion>([
            ProposalCall($$"""{"title":"Идеи","markdown":"","placement_kind":"explicit_section","parent_section_id":"{{first.Id:D}}","section_query":"Заметки"}"""),
            new("Уточните полный путь раздела «Заметки».", [])
        ]);

        var result = await CreateDocumentService(calls, [first, second]).RespondAsync(Request("Добавь в раздел «Заметки» документ «Идеи»."));

        Assert.Null(result.Proposal);
        Assert.Contains("Заметки", result.Answer);
    }

    [Fact]
    public async Task Explicit_full_section_path_disambiguates_duplicate_titles()
    {
        var first = new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 1, "Заметки", null, "Дом / Заметки", false, 0);
        var second = new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 2, "Заметки", null, "Работа / Заметки", false, 1);
        var calls = new Queue<ModelCompletion>([
            ProposalCall($$"""{"title":"Рабочие идеи","markdown":"","placement_kind":"explicit_section","parent_section_id":"{{second.Id:D}}","section_query":"Работа / Заметки"}""")
        ]);

        var result = await CreateDocumentService(calls, [first, second]).RespondAsync(Request("Добавь в раздел «Работа / Заметки» документ «Рабочие идеи»."));

        Assert.NotNull(result.Proposal);
        using var payload = JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
        Assert.Equal(second.Id, payload.RootElement.GetProperty("parentSectionId").GetGuid());
        Assert.Equal("Работа / Заметки", payload.RootElement.GetProperty("parentSectionPath").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Добавь документ «Поездка в Казань».")]
    public async Task Flat_document_tool_infers_section_from_catalog_and_keeps_empty_body(string? quote)
    {
        var sectionId = Guid.NewGuid();
        var section = new KnowledgeNodeState(KnowledgeNodeKind.Section, sectionId, null, 3, "Поездки", null, "Личное / Поездки", false, 0);
        var calls = new Queue<ModelCompletion>([
            new(null, [new ModelToolCall("sections", "list_knowledge_sections", "{}")]),
            PrepareDocumentCall(JsonSerializer.Serialize(new { title = "Поездка в Казань", markdown = "", section = "Личное / Поездки", section_quote = quote, reason = "документ о поездке" }))
        ]);

        var result = await CreateDocumentService(calls, [section]).RespondAsync(Request("Добавь документ «Поездка в Казань»."));

        Assert.NotNull(result.Proposal);
        using var payload = JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
        Assert.Equal("Поездка в Казань", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal("", payload.RootElement.GetProperty("markdown").GetString());
        Assert.Equal(sectionId, payload.RootElement.GetProperty("parentSectionId").GetGuid());
        Assert.Equal("Личное / Поездки", payload.RootElement.GetProperty("parentSectionPath").GetString());
        Assert.Equal("inferred_section", payload.RootElement.GetProperty("placementKind").GetString());
    }

    [Fact]
    public async Task Flat_document_tool_uses_the_explicit_section_from_the_user_quote()
    {
        var requestedId = Guid.NewGuid();
        var suggestedId = Guid.NewGuid();
        var sections = new[]
        {
            new KnowledgeNodeState(KnowledgeNodeKind.Section, requestedId, null, 1, "Финансы", null, "Финансы", false, 0),
            new KnowledgeNodeState(KnowledgeNodeKind.Section, suggestedId, null, 1, "Поездки", null, "Личное / Поездки", false, 1)
        };
        var calls = new Queue<ModelCompletion>([
            PrepareDocumentCall("""{"title":"Налоговый вычет","markdown":"","section":"Финансы","section_quote":"в Финансы","reason":"Пользователь указал этот раздел."}""")
        ]);

        var result = await CreateDocumentService(calls, sections).RespondAsync(Request("Добавь в Финансы документ «Налоговый вычет»."));

        Assert.NotNull(result.Proposal);
        using var payload = JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
        Assert.Equal(requestedId, payload.RootElement.GetProperty("parentSectionId").GetGuid());
        Assert.Equal("explicit_section", payload.RootElement.GetProperty("placementKind").GetString());
    }

    [Theory]
    [InlineData("Поездки")]
    [InlineData("корень")]
    public async Task Flat_document_tool_rejects_a_location_that_conflicts_with_grounded_user_quote(string modelSection)
    {
        var finance = new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 1, "Финансы", null, "Финансы", false, 0);
        var travel = new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 1, "Поездки", null, "Поездки", false, 1);
        var calls = new Queue<ModelCompletion>([
            PrepareDocumentCall($$"""{"title":"Налоговый вычет","markdown":"","section":"{{modelSection}}","section_quote":"в Финансы","reason":"model output"}"""),
            new("В предложении указан неверный раздел. Уточните расположение.", [])
        ]);

        var result = await CreateDocumentService(calls, [finance, travel])
            .RespondAsync(Request("Добавь в Финансы документ «Налоговый вычет»."));

        Assert.Null(result.Proposal);
        Assert.Contains("неверный раздел", result.Answer);
    }

    [Fact]
    public async Task Flat_document_tool_uses_root_when_catalog_has_no_suitable_section()
    {
        var calls = new Queue<ModelCompletion>([
            new(null, [new ModelToolCall("sections", "list_knowledge_sections", "{}")]),
            PrepareDocumentCall("""{"title":"Рецепт пирога","markdown":"","section":null,"section_quote":null,"reason":"в каталоге нет раздела о кулинарии"}""")
        ]);

        var result = await CreateDocumentService(calls, []).RespondAsync(Request("Добавь документ «Рецепт пирога»."));

        Assert.NotNull(result.Proposal);
        using var payload = JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
        Assert.Equal("", payload.RootElement.GetProperty("markdown").GetString());
        Assert.False(payload.RootElement.TryGetProperty("parentSectionId", out _));
        Assert.Equal("root_inferred", payload.RootElement.GetProperty("placementKind").GetString());
    }

    [Fact]
    public async Task Flat_document_tool_requires_catalog_for_inferred_placement_and_clarifies_duplicates()
    {
        var noCatalog = new Queue<ModelCompletion>([
            PrepareDocumentCall("""{"title":"Поездка","markdown":"","section":"Поездки","section_quote":null,"reason":"topic"}""")
        ]);
        var rejected = await CreateDocumentService(noCatalog,
            [new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 1, "Поездки", null, "Поездки", false, 0)])
            .RespondAsync(Request("Добавь документ «Поездка»."));
        Assert.Null(rejected.Proposal);
        Assert.Contains("Сначала вызови list_knowledge_sections", rejected.Answer);

        var first = new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 1, "Заметки", null, "Дом / Заметки", false, 0);
        var second = new KnowledgeNodeState(KnowledgeNodeKind.Section, Guid.NewGuid(), null, 1, "Заметки", null, "Работа / Заметки", false, 1);
        var duplicate = new Queue<ModelCompletion>([
            PrepareDocumentCall("""{"title":"Идеи","markdown":"","section":"Заметки","section_quote":"в раздел «Заметки»","reason":""}"""),
            new("Какой путь выбрать: Дом / Заметки или Работа / Заметки?", [])
        ]);
        var clarification = await CreateDocumentService(duplicate, [first, second])
            .RespondAsync(Request("Добавь в раздел «Заметки» документ «Идеи»."));
        Assert.Null(clarification.Proposal);
        Assert.Contains("Дом / Заметки", clarification.Answer);
        Assert.Contains("Работа / Заметки", clarification.Answer);
    }

    private static ModelCompletion PrepareDocumentCall(string arguments) => new(null,
        [new ModelToolCall("prepare", "prepare_knowledge_document", arguments)]);

    private static AgentTurnRequest Request(string prompt) =>
        new(Guid.NewGuid(), Guid.NewGuid(), prompt, new AgentScope("general", null, null, null),
            "Gemma", PersonalDashboard.V2.Contracts.Chat.ChatModelRoute.Default, []);

    private static ModelCompletion ProposalCall(string after) => new(null,
        [new ModelToolCall("proposal", "propose_changes",
            """{"changes":[{"module":"Knowledge","operation":"Create","entityType":"knowledge.document","after":""" + after + "}]}")]);

    private static AgentTurnService CreateDocumentService(Queue<ModelCompletion> calls, IReadOnlyList<KnowledgeNodeState> sections) =>
        new(new QueueRouter(calls), new EmptySearch(), new KnowledgeAccess([], sections), new EmptyPlanning(), new EmptyTasks());

    private sealed class QueueRouter(Queue<ModelCompletion> calls) : IChatModelRouter
    {
        public Task<RoutedCompletion> CompleteAsync(string requestedModel, ModelCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new RoutedCompletion(calls.Count > 0 ? calls.Dequeue() : new ModelCompletion(request.Messages[^1].Content, []), requestedModel, requestedModel, null));
    }

    [Fact]
    public async Task Confirmation_rejects_a_section_whose_version_or_path_changed_after_preview()
    {
        var sectionId = Guid.NewGuid();
        var store = new Store();
        var transactions = new RollbackRunner();
        var knowledge = new KnowledgeAccess(transactions.Writes)
        {
            ReadState = new KnowledgeNodeState(KnowledgeNodeKind.Section, sectionId, null, 2, "Travel", null, "Personal / Travel", false, 0)
        };
        var service = new ProposalConfirmationService(store, transactions, knowledge, new EmptyPlanning(), new EmptyTasks());
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            title = "Trip", markdown = "", parentSectionId = sectionId,
            expectedParentVersion = 1, parentSectionPath = "Personal / Travel", placementReason = "inferred"
        })).RootElement.Clone();
        store.Proposal = Proposal([new ChatProposedAction(Guid.NewGuid(), "knowledge.document", Guid.NewGuid(), "Create", null,
            payload, "Trip", null, payload)]);

        var result = await service.ConfirmAsync(store.Proposal.Id, Guid.NewGuid());

        Assert.False(result.Applied);
        Assert.True(result.Stale);
        Assert.Empty(transactions.Writes);
    }

    [Theory]
    [InlineData("Other / Travel", false)]
    [InlineData("Personal / Travel", true)]
    public async Task Confirmation_rejects_a_section_whose_path_changed_or_was_archived(string currentPath, bool archived)
    {
        var sectionId = Guid.NewGuid();
        var store = new Store();
        var transactions = new RollbackRunner();
        var knowledge = new KnowledgeAccess(transactions.Writes)
        {
            ReadState = new KnowledgeNodeState(KnowledgeNodeKind.Section, sectionId, null, 1, "Travel", null, currentPath, archived, 0)
        };
        var service = new ProposalConfirmationService(store, transactions, knowledge, new EmptyPlanning(), new EmptyTasks());
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            title = "Trip", markdown = "", parentSectionId = sectionId,
            expectedParentVersion = 1, parentSectionPath = "Personal / Travel", placementReason = "inferred", placementKind = "inferred_section"
        })).RootElement.Clone();
        store.Proposal = Proposal([new ChatProposedAction(Guid.NewGuid(), "knowledge.document", Guid.NewGuid(), "Create", null,
            payload, "Trip", null, payload)]);

        var result = await service.ConfirmAsync(store.Proposal.Id, Guid.NewGuid());

        Assert.False(result.Applied);
        Assert.True(result.Stale);
        Assert.Empty(transactions.Writes);
    }

    [Theory]
    [InlineData(2, "Продукт")]
    [InlineData(1, "Продукт нового названия")]
    public async Task Task_confirmation_rejects_changed_feature_chain_before_applying(long currentProjectVersion, string currentProjectTitle)
    {
        var projectId = Guid.NewGuid();
        var milestoneId = Guid.NewGuid();
        var featureId = Guid.NewGuid();
        var store = new Store();
        var transactions = new RollbackRunner();
        var tasks = new CountingTasks();
        var planning = new PlanningStates(
            new PlanningEntityState(PlanningEntityKind.Project, projectId, null, null, currentProjectVersion, currentProjectTitle, null, null, false, 0),
            new PlanningEntityState(PlanningEntityKind.Milestone, milestoneId, projectId, null, 1, "Безопасность", null, null, false, 0),
            new PlanningEntityState(PlanningEntityKind.Feature, featureId, projectId, milestoneId, 1, "Авторизация", null, "planned", false, 0));
        var service = new ProposalConfirmationService(store, transactions, new KnowledgeAccess([]), planning, tasks);
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            title = "Проверить вход", description = "", placement = "planned",
            planning = new { projectId, milestoneId, featureId },
            featurePath = "Продукт / Безопасность / Авторизация",
            expectedProjectVersion = 1, expectedMilestoneVersion = 1, expectedFeatureVersion = 1,
            taskPlacementReason = "test"
        })).RootElement.Clone();
        store.Proposal = Proposal([new ChatProposedAction(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null,
            payload, "Проверить вход", null, payload)]);

        var result = await service.ConfirmAsync(store.Proposal.Id, Guid.NewGuid());

        Assert.False(result.Applied);
        Assert.True(result.Stale);
        Assert.Equal(0, tasks.ApplyCalls);
    }

    [Fact]
    public async Task Task_confirmation_rejects_changed_backlog_section_version_before_applying()
    {
        var sectionId = Guid.NewGuid();
        var store = new Store();
        var tasks = new CountingTasks([new TaskBacklogSection(sectionId, "Покупки", 2)]);
        var service = new ProposalConfirmationService(store, new RollbackRunner(), new KnowledgeAccess([]), new EmptyPlanning(), tasks);
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            title = "Купить билеты", description = "", placement = "backlog", sectionId,
            backlogSectionName = "Покупки", expectedBacklogSectionVersion = 1,
            featurePath = (string?)null, taskPlacementReason = "test"
        })).RootElement.Clone();
        store.Proposal = Proposal([new ChatProposedAction(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null,
            payload, "Купить билеты", null, payload)]);

        var result = await service.ConfirmAsync(store.Proposal.Id, Guid.NewGuid());

        Assert.False(result.Applied);
        Assert.True(result.Stale);
        Assert.Equal(0, tasks.ApplyCalls);
    }

    [Fact]
    public async Task Fake_transaction_boundary_rolls_back_task_create_when_linked_backlog_move_fails()
    {
        var projectId = Guid.NewGuid();
        var milestoneId = Guid.NewGuid();
        var featureId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var repository = new FailingTaskRepository(sectionId) { FailOnSaveAttempt = 2 };
        var transactions = new SnapshotTransactionRunner(repository);
        var taskAccess = new TasksAgentAccess(new TasksService(repository, new ValidPlanningLinks(), transactions));
        var store = new Store();
        var planning = new PlanningStates(
            new PlanningEntityState(PlanningEntityKind.Project, projectId, null, null, 1, "Продукт", null, null, false, 0),
            new PlanningEntityState(PlanningEntityKind.Milestone, milestoneId, projectId, null, 1, "Безопасность", null, null, false, 0),
            new PlanningEntityState(PlanningEntityKind.Feature, featureId, projectId, milestoneId, 1, "Авторизация", null, "planned", false, 0));
        var service = new ProposalConfirmationService(store, transactions, new KnowledgeAccess([]), planning, taskAccess);
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            title = "Проверить вход", description = "", placement = "backlog",
            planning = new { projectId, milestoneId, featureId },
            featurePath = "Продукт / Безопасность / Авторизация",
            expectedProjectVersion = 1, expectedMilestoneVersion = 1, expectedFeatureVersion = 1,
            taskPlacementReason = "Сразу в Backlog по просьбе пользователя."
        })).RootElement.Clone();
        store.Proposal = Proposal([new ChatProposedAction(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null,
            payload, "Проверить вход", null, payload)]);

        var result = await service.ConfirmAsync(store.Proposal.Id, Guid.NewGuid());

        Assert.False(result.Applied);
        Assert.True(result.Stale);
        Assert.Equal(1, transactions.RollbackCount);
        Assert.Empty(repository.Items);
        Assert.Equal(ChatProposalState.Dismissed, store.Proposal.State);
    }

    [Fact]
    public async Task Turn_without_ready_replacement_preserves_pending_draft_for_revision_or_unrelated_question()
    {
        var conversationId = Guid.NewGuid();
        var store = new Store { Proposal = Proposal([Action("Draft")]) with { ConversationId = conversationId } };
        var pendingId = store.Proposal.Id;

        await PersonalDashboard.V2.Agent.Api.ProposalDraftPersistence.PersistTurnAsync(
            store, new RollbackRunner(), Turn(conversationId), null);

        Assert.Equal(ChatProposalState.Pending, store.Proposal!.State);
        Assert.Equal(pendingId, store.Proposal.Id);
        Assert.Empty(store.DismissedProposals);
        Assert.Empty(store.SavedProposals);
        Assert.Single(store.Turns);
    }

    [Fact]
    public async Task Ready_replacement_dismisses_old_draft_and_saves_new_draft_in_same_transaction()
    {
        var conversationId = Guid.NewGuid();
        var store = new Store { Proposal = Proposal([Action("Old")]) with { ConversationId = conversationId } };
        var oldId = store.Proposal.Id;
        var turn = Turn(conversationId);
        var replacement = Proposal([Action("Revised")]) with { ConversationId = conversationId, TurnId = turn.Id };

        await PersonalDashboard.V2.Agent.Api.ProposalDraftPersistence.PersistTurnAsync(
            store, new RollbackRunner(), turn, replacement);

        Assert.Equal(oldId, Assert.Single(store.DismissedProposals).Id);
        Assert.Equal(replacement.Id, Assert.Single(store.SavedProposals).Id);
        Assert.Equal(ChatProposalState.Pending, store.Proposal!.State);
        Assert.Equal(turn.Id, store.Proposal.TurnId);
    }

    private static ChatTurn Turn(Guid conversationId) => new(
        Guid.NewGuid(), conversationId, "user", "assistant",
        new ChatTurnScope("general", null, null, null), "Gemma", "Gemma",
        ChatModelRoute.Default, [], DateTimeOffset.UtcNow);

    private static ChatProposal Proposal(IReadOnlyList<ChatProposedAction> actions) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), actions, ChatProposalState.Pending, null, DateTimeOffset.UtcNow);

    private static ChatProposedAction Action(string title) => new(Guid.NewGuid(), "knowledge.document", Guid.NewGuid(),
        "Create", 1, JsonDocument.Parse($"{{\"title\":\"{title}\",\"markdown\":\"body\"}}").RootElement.Clone(), title, null, null);

    private sealed class Provider(string model, ModelCompletion? result) : IChatModelProvider
    {
        public string Model { get; } = model;
        public ModelCompletion? Result { get; set; } = result;
        public Exception? Error { get; set; }
        public int Calls { get; private set; }
        public Task<ModelCompletion> CompleteAsync(ModelCompletionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Error is not null) throw Error;
            return Task.FromResult(Result ?? throw new InvalidOperationException("no completion"));
        }
    }

    private sealed class RollbackRunner : ITransactionRunner
    {
        public List<Guid> Writes { get; } = [];
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            await ExecuteAsync(async ct => { await operation(ct); return true; }, cancellationToken);
        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            var before = Writes.Count;
            try { return await operation(cancellationToken); }
            catch { Writes.RemoveRange(before, Writes.Count - before); throw; }
        }
    }

    private sealed class Store : IChatConversationStore
    {
        public ChatProposal? Proposal { get; set; }
        public List<ChatTurn> Turns { get; } = [];
        public List<ChatProposal> DismissedProposals { get; } = [];
        public List<ChatProposal> SavedProposals { get; } = [];
        public Task<ChatConversationState?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<ChatConversationState?>(null);
        public Task<ChatConversationState> CreateConversationAsync(Guid id, string? title, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChatConversationPage> ListConversationsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteConversationAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ChatTurn>> GetRecentTurnsAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChatTurnPage> GetTurnsPageAsync(Guid conversationId, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task AppendTurnAsync(ChatTurn turn, CancellationToken cancellationToken = default) { Turns.Add(turn); return Task.CompletedTask; }
        public Task<ChatProposal?> GetProposalAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Proposal?.Id == id ? Proposal : null);
        public Task<ChatProposal?> GetProposalForTurnAsync(Guid turnId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChatProposal?> GetPendingProposalAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Proposal?.ConversationId == conversationId && Proposal.State == ChatProposalState.Pending ? Proposal : null);
        public Task SaveProposalAsync(ChatProposal proposal, CancellationToken cancellationToken = default) { SavedProposals.Add(proposal); Proposal = proposal; return Task.CompletedTask; }
        public Task<int> DismissPendingProposalsAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            if (Proposal?.ConversationId != conversationId || Proposal.State != ChatProposalState.Pending) return Task.FromResult(0);
            DismissedProposals.Add(Proposal);
            Proposal = Proposal with { State = ChatProposalState.Dismissed };
            return Task.FromResult(1);
        }
        public Task<bool> TryChangeProposalStateAsync(Guid id, ChatProposalState expectedState, ChatProposalState newState, Guid? confirmationId, CancellationToken cancellationToken = default)
        {
            if (Proposal?.Id != id || Proposal.State != expectedState) return Task.FromResult(false);
            Proposal = Proposal with { State = newState, ConfirmationId = confirmationId };
            return Task.FromResult(true);
        }
    }

    private sealed class KnowledgeAccess(List<Guid> writes, IReadOnlyList<KnowledgeNodeState>? sections = null) : IKnowledgeAgentAccess
    {
        private readonly IReadOnlyList<KnowledgeNodeState> _sections = sections ?? [];
        public Task<IReadOnlyList<KnowledgeNodeState>> ListSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_sections);
        public Guid? StaleId { get; set; }
        public KnowledgeNodeState? ReadState { get; set; }
        public Task<KnowledgeNodeState?> ReadAsync(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult(ReadState?.Id == id ? ReadState : null);
        public Task<KnowledgeMutationResult> ApplyAsync(KnowledgeMutation mutation, CancellationToken cancellationToken = default)
        {
            if (mutation.Id == StaleId) return Task.FromResult(new KnowledgeMutationResult(false, null, "stale"));
            writes.Add(mutation.Id);
            return Task.FromResult(new KnowledgeMutationResult(true, null, null));
        }
    }

    private sealed class EmptyPlanning : IPlanningAgentAccess
    {
        public Task<IReadOnlyList<TaskFeatureTarget>> ListTaskFeaturesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskFeatureTarget>>([]);
        public Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class PlanningStates(params PlanningEntityState[] states) : IPlanningAgentAccess
    {
        public Task<IReadOnlyList<TaskFeatureTarget>> ListTaskFeaturesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskFeatureTarget>>([]);
        public Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(states.SingleOrDefault(state => state.Kind == kind && state.Id == id));
        public Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class CountingTasks(IReadOnlyList<TaskBacklogSection>? sections = null) : ITasksAgentAccess
    {
        private readonly IReadOnlyList<TaskBacklogSection> _sections = sections ?? [];
        public int ApplyCalls { get; private set; }
        public Task<IReadOnlyList<TaskBacklogSection>> ListBacklogSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_sections);
        public Task<TaskEntityState?> ReadAsync(TaskEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskEntityState?>(null);
        public Task<TaskMutationResult> ApplyAsync(TaskMutation mutation, CancellationToken cancellationToken = default) { ApplyCalls++; return Task.FromResult(new TaskMutationResult(true, null, null)); }
    }

    private sealed class ValidPlanningLinks : IPlanningLinkValidator
    {
        public Task<PlanningLinkValidationResult> ValidateAsync(PlanningLink link, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlanningLinkValidationResult(true, []));
    }
    private sealed class SnapshotTransactionRunner(FailingTaskRepository repository) : ITransactionRunner
    {
        public int RollbackCount { get; private set; }
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            await ExecuteAsync(async ct => { await operation(ct); return true; }, cancellationToken);
        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            var snapshot = repository.Snapshot();
            try { return await operation(cancellationToken); }
            catch { RollbackCount++; repository.Restore(snapshot); throw; }
        }
    }
    private sealed class FailingTaskRepository(Guid backlogSectionId) : ITasksRepository
    {
        private readonly Dictionary<Guid, TaskItem> _items = [];
        private readonly Dictionary<Guid, TaskSection> _sections = new() { [backlogSectionId] = new TaskSection("Общее", TaskLocation.Backlog, backlogSectionId) };
        private int _saveAttempts;
        public int FailOnSaveAttempt { get; init; }
        public IReadOnlyCollection<TaskItem> Items => _items.Values.ToArray();
        public Dictionary<Guid, TaskItem> Snapshot() => new(_items);
        public void Restore(Dictionary<Guid, TaskItem> snapshot) { _items.Clear(); foreach (var pair in snapshot) _items.Add(pair.Key, pair.Value); }
        public Task<IReadOnlyList<TaskItem>> ListAsync(TaskLocation? location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskItem>>(_items.Values.Where(item => location is null || item.Location == location).ToArray());
        public Task<TaskItem?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(_items.GetValueOrDefault(id));
        public Task SaveAsync(TaskItem item, CancellationToken ct)
        {
            if (++_saveAttempts == FailOnSaveAttempt) throw new InvalidOperationException("simulated second-write failure");
            _items[item.Id] = item;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken ct) { _items.Remove(id); return Task.CompletedTask; }
        public Task<IReadOnlyList<TaskSection>> ListSectionsAsync(TaskLocation location, CancellationToken ct) => Task.FromResult<IReadOnlyList<TaskSection>>(_sections.Values.Where(section => section.Location == location).ToArray());
        public Task SaveSectionAsync(TaskSection section, CancellationToken ct) { _sections[section.Id] = section; return Task.CompletedTask; }
        public Task DeleteSectionAsync(Guid id, CancellationToken ct) { _sections.Remove(id); return Task.CompletedTask; }
    }

    private sealed class EmptyTasks : ITasksAgentAccess
    {
        public Task<IReadOnlyList<TaskBacklogSection>> ListBacklogSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskBacklogSection>>([]);
        public Task<TaskEntityState?> ReadAsync(TaskEntityKind kind, Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<TaskMutationResult> ApplyAsync(TaskMutation mutation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FixedRouter(string arguments) : IChatModelRouter
    {
        public Task<RoutedCompletion> CompleteAsync(string requestedModel, ModelCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new RoutedCompletion(new ModelCompletion(null,
                [new ModelToolCall("call-1", "propose_changes", arguments)]), requestedModel, requestedModel, null));
    }

    private sealed class EmptySearch : ISearchService
    {
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResponse([], null, true, null));
    }
}

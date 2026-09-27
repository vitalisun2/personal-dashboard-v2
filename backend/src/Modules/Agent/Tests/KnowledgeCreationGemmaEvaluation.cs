using Microsoft.Extensions.Configuration;
using PersonalDashboard.V2.Agent.Application;
using PersonalDashboard.V2.Agent.Infrastructure;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Search;
using Xunit;

namespace PersonalDashboard.V2.Agent.Tests;

public sealed class KnowledgeCreationGemmaEvaluation
{
    private static readonly Guid TravelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FinanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [GemmaFact]
    public async Task Real_Gemma_handles_document_creation_and_draft_conversation_without_persistent_writes()
    {
        var cases = new[]
        {
            ("title-only inferred section", "Добавь документ «Подготовка к поездке в Казань».", "Подготовка к поездке в Казань", (Guid?)TravelId, "inferred_section", ""),
            ("explicit section and title without body", "Добавь в раздел «Финансы» документ «Налоговый вычет».", "Налоговый вычет", (Guid?)FinanceId, "explicit_section", ""),
            ("explicit body cleanup", "Добавь в Путешествия документ «Поездка в Казань» и запиши внутрь: эээ проверить билеты и забронировать отель.", "Поездка в Казань", (Guid?)TravelId, "explicit_section", "проверить билеты"),
            ("no suitable section uses root", "Создай документ «Рецепт яблочного пирога».", "Рецепт яблочного пирога", null, "root_inferred", ""),
        };

        foreach (var (name, prompt, expectedTitle, expectedParent, expectedPlacement, bodyContains) in cases)
        {
            Console.WriteLine("Gemma evaluation case: " + name);
            var result = await CreateService().RespondAsync(Request(prompt));
            Assert.True(result.Proposal is not null, name + ": model did not prepare a proposal. " + result.Answer);
            using var payload = System.Text.Json.JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
            Assert.Equal(expectedTitle, payload.RootElement.GetProperty("title").GetString());
            Assert.Equal(expectedParent?.ToString(), payload.RootElement.TryGetProperty("parentSectionId", out var parent) ? parent.GetString() : null);
            Assert.Equal(expectedPlacement, payload.RootElement.GetProperty("placementKind").GetString());
            var body = payload.RootElement.GetProperty("markdown").GetString()!;
            if (bodyContains.Length == 0)
                Assert.Equal("", body);
            else
            {
                Assert.Contains(bodyContains, body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("добавь", body, StringComparison.OrdinalIgnoreCase);
            }
        }

        var clarification = await CreateService().RespondAsync(Request("Добавь документ в раздел «Путешествия»."));
        Assert.Null(clarification.Proposal);

        var ambiguousSections = new[]
        {
            new KnowledgeNodeState(KnowledgeNodeKind.Section, TravelId, null, 1, "Заметки", null, "Личное / Заметки", false, 0),
            new KnowledgeNodeState(KnowledgeNodeKind.Section, FinanceId, null, 1, "Заметки", null, "Работа / Заметки", false, 1),
        };
        var ambiguous = await CreateService(ambiguousSections).RespondAsync(Request("Добавь в раздел «Заметки» документ «Идеи»."));
        Assert.Null(ambiguous.Proposal);

        var priorDraft = "{\"title\":\"Черновое название\",\"markdown\":\"проверить билеты\",\"parentSectionId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"parentSectionPath\":\"Путешествия\",\"expectedParentVersion\":1,\"placementKind\":\"inferred_section\",\"placementReason\":\"Документ о поездке\"}";
        var revision = await CreateService().RespondAsync(Request("Переименуй этот документ в «Поездка в Казань».",
            [DraftContext(priorDraft),
             new ModelMessage("assistant", "Предлагаю документ «Черновое название».")], DraftProposal(priorDraft)));
        Assert.NotNull(revision.Proposal);
        var revisionPayload = System.Text.Json.JsonDocument.Parse(Assert.Single(revision.Proposal!.Changes).AfterJson);
        Assert.Equal("Поездка в Казань", revisionPayload.RootElement.GetProperty("title").GetString());
        Assert.Contains("проверить билеты", revisionPayload.RootElement.GetProperty("markdown").GetString());

        var confirmationText = await CreateService().RespondAsync(Request("Да, создавай.",
            [DraftContext(priorDraft),
             new ModelMessage("assistant", "Нажмите кнопку «Создать документ» для подтверждения.")], DraftProposal(priorDraft)));
        Assert.Null(confirmationText.Proposal);

        var unrelated = await CreateService().RespondAsync(Request("Какая сейчас погода?",
            [DraftContext(priorDraft)], DraftProposal(priorDraft)));
        Assert.Null(unrelated.Proposal);
    }

    private static ModelMessage DraftContext(string json) => ProposalDraftContext.ToMessage(DraftProposal(json));

    private static ChatProposal DraftProposal(string json)
    {
        var payload = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        return new ChatProposal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            [new ChatProposedAction(Guid.NewGuid(), "knowledge.document", Guid.NewGuid(), "Create", null, payload, "Draft", null, payload)],
            ChatProposalState.Pending, null, DateTimeOffset.UtcNow);
    }

    private static AgentTurnService CreateService(IReadOnlyList<KnowledgeNodeState>? sections = null)
    {
        var configuration = new ConfigurationManager
        {
            ["OLLAMA_URL"] = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434",
            ["OLLAMA_CHAT_MODEL"] = Environment.GetEnvironmentVariable("OLLAMA_CHAT_MODEL") ?? "gemma4:e4b-it-qat"
        };
        var provider = new OllamaChatModelProvider(new HttpFactory(), configuration);
        return new AgentTurnService(new TracingRouter(new ChatModelRouter([provider])), new EmptySearch(), new Catalog(sections ??
        [
            new KnowledgeNodeState(KnowledgeNodeKind.Section, TravelId, null, 1, "Путешествия", null, "Путешествия", false, 0),
            new KnowledgeNodeState(KnowledgeNodeKind.Section, FinanceId, null, 1, "Финансы", null, "Финансы", false, 1),
        ]), new EmptyPlanning(), new EmptyTasks());
    }

    private static AgentTurnRequest Request(string prompt, IReadOnlyList<ModelMessage>? recent = null, ChatProposal? pending = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), prompt, new AgentScope("general", null, null, null),
            "Gemma", ChatModelRoute.Default, recent ?? [], PendingProposal: pending);

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TracingRouter(IChatModelRouter inner) : IChatModelRouter
    {
        public async Task<RoutedCompletion> CompleteAsync(string requestedModel, ModelCompletionRequest request, CancellationToken cancellationToken)
        {
            Console.WriteLine("MODEL INPUT: " + request.Messages.Last().Content);
            var result = await inner.CompleteAsync(requestedModel, request, cancellationToken);
            Console.WriteLine("MODEL OUTPUT: " + System.Text.Json.JsonSerializer.Serialize(result.Completion));
            return result;
        }
    }

    private sealed class Catalog(IReadOnlyList<KnowledgeNodeState> sections) : IKnowledgeAgentAccess
    {
        public Task<IReadOnlyList<KnowledgeNodeState>> ListSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(sections);
        public Task<KnowledgeNodeState?> ReadAsync(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<KnowledgeNodeState?>(sections.FirstOrDefault(section => section.Id == id));
        public Task<KnowledgeMutationResult> ApplyAsync(KnowledgeMutation mutation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Evaluation fixtures never persist data.");
    }

    private sealed class EmptySearch : ISearchService
    {
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResponse([], null, true, null));
    }

    private sealed class EmptyPlanning : IPlanningAgentAccess
    {
        public Task<IReadOnlyList<TaskFeatureTarget>> ListTaskFeaturesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskFeatureTarget>>([]);
        public Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<PlanningEntityState?>(null);
        public Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class EmptyTasks : ITasksAgentAccess
    {
        public Task<IReadOnlyList<TaskBacklogSection>> ListBacklogSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskBacklogSection>>([]);
        public Task<TaskEntityState?> ReadAsync(TaskEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskEntityState?>(null);
        public Task<TaskMutationResult> ApplyAsync(TaskMutation mutation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

public sealed class GemmaFactAttribute : FactAttribute
{
    public GemmaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PERSONAL_DASHBOARD_GEMMA_EVAL") != "1")
            Skip = "Set PERSONAL_DASHBOARD_GEMMA_EVAL=1 to evaluate real local Gemma with synthetic data.";
    }
}

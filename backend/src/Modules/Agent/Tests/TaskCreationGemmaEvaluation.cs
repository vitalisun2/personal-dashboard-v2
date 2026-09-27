using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PersonalDashboard.V2.Agent.Application;
using PersonalDashboard.V2.Agent.Infrastructure;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Search;
using Xunit;

namespace PersonalDashboard.V2.Agent.Tests;

// Opt-in evaluation against the local model. All catalogs are synthetic; writes always throw.
public sealed class TaskCreationGemmaEvaluation
{
    private static readonly Guid PersonalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GeneralId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly TaskFeatureTarget Auth = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Кабинет", 1,
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Первый релиз", 1,
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "Авторизация", 1, "Кабинет / Первый релиз / Авторизация");

    [GemmaFact]
    public async Task Real_Gemma_routes_tasks_using_messages_and_reviewed_drafts()
    {
        var cases = new[]
        {
            new Case("default_backlog", "Добавь задачку «Купить молоко».", "Купить молоко", "backlog", PersonalId),
            new Case("explicit_section", "Добавь в раздел бэклога «Общее» задачку «Позвонить в сервис».", "Позвонить в сервис", "backlog", GeneralId),
            new Case("feature_plan", "Добавь задачку «Проверить восстановление пароля» в фичу авторизации.", "Проверить восстановление пароля", "planned", Feature: Auth.FeatureId),
            new Case("feature_backlog", "В фичу «Авторизация» добавь задачу «Проверить восстановление пароля» и сразу в бэклог.", "Проверить восстановление пароля", "backlog", Feature: Auth.FeatureId),
            new Case("page_does_not_route", "Добавь задачку «Купить молоко».", "Купить молоко", "backlog", PersonalId, Focus: true),
            new Case("project_only", "Добавь в проект «Кабинет» задачу «Проверить восстановление пароля».", null, null),
            new Case("unknown_feature", "Добавь задачу «Проверить письма» в фичу «Почтовые рассылки».", null, null),
            new Case("duplicate_feature", "Добавь в фичу «Авторизация» задачу «Проверить пароль».", null, null, Duplicate: true),
            new Case("conversation_reference", "Добавь туда задачу «Проверить пароль».", "Проверить пароль", "planned", Feature: Auth.FeatureId,
                History: [new("user", "Работаем с фичей «Авторизация» проекта «Кабинет», эпик «Первый релиз»."), new("assistant", "Обсуждаем фичу «Кабинет / Первый релиз / Авторизация».")]),
            new Case("dictated_description", "Добавь задачу «Позвонить в сервис». В описании укажи: эээ узнать стоимость замены аккумулятора.", "Позвонить в сервис", "backlog", PersonalId, Body: "узнать стоимость замены аккумулятора")
        };
        var selected = Environment.GetEnvironmentVariable("GEMMA_TASK_CASE");
        var failures = new List<string>();
        foreach (var example in cases.Where(item => string.IsNullOrEmpty(selected) || item.Name == selected))
        {
            Console.WriteLine("TASK CASE: " + example.Name);
            try
            {
                var features = example.Duplicate ? new[] { Auth, Auth with { ProjectId = Guid.NewGuid(), ProjectTitle = "Магазин", FeatureId = Guid.NewGuid(), Path = "Магазин / Первый релиз / Авторизация" } } : [Auth];
                var scope = example.Focus ? new AgentScope("entity", "planning.feature", Auth.FeatureId, 1) : new("general", null, null, null);
                var result = await Service(features).RespondAsync(new(Guid.NewGuid(), Guid.NewGuid(), example.Prompt, scope,
                    "Gemma", ChatModelRoute.Default, example.History ?? []));
                if (example.Title is null) { Assert.Null(result.Proposal); Assert.False(string.IsNullOrWhiteSpace(result.Answer)); continue; }
                Assert.True(result.Proposal is not null, result.Answer);
                using var json = JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
                var payload = json.RootElement;
                Assert.Equal(example.Title, payload.GetProperty("title").GetString());
                Assert.Equal(example.Destination, payload.GetProperty("placement").GetString());
                Assert.Equal(example.Section?.ToString(), Text(payload, "sectionId"));
                Assert.Equal(example.Feature?.ToString(), payload.TryGetProperty("planning", out var link) && link.ValueKind == JsonValueKind.Object ? Text(link, "featureId") : null);
                if (example.Body is null) Assert.Equal("", Text(payload, "description"));
                else Assert.Contains(example.Body, Text(payload, "description")!, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) { failures.Add(example.Name + ": " + error.Message); Console.WriteLine("TASK FAILURE: " + example.Name + " " + error.Message); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [GemmaFact]
    public async Task Real_Gemma_revises_pending_tasks_without_saving_by_text()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            title = "Проверить пароль", description = "Проверить письмо со ссылкой.", placement = "planned",
            planning = new { projectId = Auth.ProjectId, milestoneId = Auth.MilestoneId, featureId = Auth.FeatureId },
            featurePath = Auth.Path, expectedProjectVersion = 1, expectedMilestoneVersion = 1, expectedFeatureVersion = 1
        });
        var pending = new ChatProposal(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            [new(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null, payload, "Проверить пароль", null, payload)],
            ChatProposalState.Pending, null, DateTimeOffset.UtcNow);
        var failures = new List<string>();
        foreach (var (name, prompt) in new[]
        {
            ("rename", "Назови эту задачу «Проверить восстановление пароля»."),
            ("clear_body", "Убери описание у этой задачи."),
            ("unlink", "Создай эту задачу без привязки к фиче, в разделе бэклога «Общее»."),
            ("confirm_text", "Да, добавляй.")
        })
        {
            var selected = Environment.GetEnvironmentVariable("GEMMA_TASK_CASE");
            if (!string.IsNullOrEmpty(selected) && selected != name) continue;
            Console.WriteLine("TASK REVISION: " + name);
            try
            {
                var result = await Service([Auth]).RespondAsync(new(pending.ConversationId, Guid.NewGuid(), prompt,
                    new("general", null, null, null), "Gemma", ChatModelRoute.Default, [ProposalDraftContext.ToMessage(pending)], PendingProposal: pending));
                if (name == "confirm_text") { Assert.Null(result.Proposal); continue; }
                Assert.True(result.Proposal is not null, result.Answer);
                using var json = JsonDocument.Parse(Assert.Single(result.Proposal!.Changes).AfterJson);
                var revised = json.RootElement;
                Assert.Equal(name == "rename" ? "Проверить восстановление пароля" : "Проверить пароль", Text(revised, "title"));
                Assert.Equal(name == "clear_body" ? "" : "Проверить письмо со ссылкой.", Text(revised, "description"));
                Assert.Equal(name == "unlink" ? "backlog" : "planned", Text(revised, "placement"));
                if (name == "unlink") { Assert.Equal(GeneralId.ToString(), Text(revised, "sectionId")); Assert.Null(Text(revised, "featurePath")); }
                else Assert.Equal(Auth.Path, Text(revised, "featurePath"));
            }
            catch (Exception error) { failures.Add(name + ": " + error.Message); Console.WriteLine("TASK FAILURE: " + name + " " + error.Message); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [GemmaFact]
    public async Task Real_Gemma_respects_chat_area_for_creation_and_clarification()
    {
        var examples = new[]
        {
            (Area: "knowledge", Prompt: "Создай документ с названием «Заметки о поездке».", Title: "Заметки о поездке", Placement: (string?)null),
            (Area: "tasks", Prompt: "Добавь задачу «Позвонить в сервис».", Title: "Позвонить в сервис", Placement: (string?)"backlog"),
            (Area: "planning", Prompt: "Добавь задачу «Проверить пароль» в фичу «Авторизация».", Title: "Проверить пароль", Placement: (string?)"planned"),
            (Area: "planning", Prompt: "Добавь задачу «Позвонить в сервис».", Title: (string?)null, Placement: (string?)null),
        };
        var failures = new List<string>();
        foreach (var example in examples)
        {
            Console.WriteLine($"SCOPED CASE: {example.Area}: {example.Prompt}");
            try
            {
                var result = await Service([Auth]).RespondAsync(new(Guid.NewGuid(), Guid.NewGuid(), example.Prompt,
                    new("general", null, null, null, example.Area), "Gemma", ChatModelRoute.Default, []));
                if (example.Title is null)
                {
                    Assert.Null(result.Proposal);
                    Assert.Contains("фич", result.Answer, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                Assert.True(result.Proposal is not null, result.Answer);
                var change = Assert.Single(result.Proposal!.Changes);
                Assert.Equal(example.Area == "knowledge" ? "knowledge.document" : "tasks.task", change.Target.EntityType);
                using var json = JsonDocument.Parse(change.AfterJson);
                Assert.Equal(example.Title, Text(json.RootElement, "title"));
                Assert.Equal("", Text(json.RootElement, example.Area == "knowledge" ? "markdown" : "description"));
                if (example.Placement is not null)
                    Assert.Equal(example.Placement, Text(json.RootElement, "placement"));
                if (example.Area == "planning")
                    Assert.Equal(Auth.FeatureId.ToString(), Text(json.RootElement.GetProperty("planning"), "featureId"));
            }
            catch (Exception error)
            {
                failures.Add($"{example.Area}: {example.Prompt} — {error.Message}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private sealed record Case(string Name, string Prompt, string? Title, string? Destination, Guid? Section = null, Guid? Feature = null,
        bool Focus = false, bool Duplicate = false, IReadOnlyList<ModelMessage>? History = null, string? Body = null);
    private static AgentTurnService Service(IReadOnlyList<TaskFeatureTarget> features)
    {
        var configuration = new ConfigurationManager { ["OLLAMA_URL"] = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434",
            ["OLLAMA_CHAT_MODEL"] = Environment.GetEnvironmentVariable("OLLAMA_CHAT_MODEL") ?? "gemma4:e4b-it-qat" };
        return new(new TraceRouter(new ChatModelRouter([new OllamaChatModelProvider(new HttpFactory(), configuration)])),
            new EmptySearch(), new Knowledge(), new Planning(features), new Tasks());
    }
    private sealed class HttpFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }
    private sealed class TraceRouter(IChatModelRouter inner) : IChatModelRouter
    {
        public async Task<RoutedCompletion> CompleteAsync(string model, ModelCompletionRequest request, CancellationToken ct)
        {
            Console.WriteLine("TASK INPUT: " + JsonSerializer.Serialize(request.Messages.Last().Content));
            var result = await inner.CompleteAsync(model, request, ct);
            Console.WriteLine("TASK OUTPUT: " + JsonSerializer.Serialize(result.Completion));
            return result;
        }
    }
    private sealed class Tasks : ITasksAgentAccess
    {
        public Task<IReadOnlyList<TaskBacklogSection>> ListBacklogSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskBacklogSection>>([new(PersonalId, "Личное", 1), new(GeneralId, "Общее", 1)]);
        public Task<TaskEntityState?> ReadAsync(TaskEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskEntityState?>(null);
        public Task<TaskMutationResult> ApplyAsync(TaskMutation mutation, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic evaluation never writes data.");
    }
    private sealed class Planning(IReadOnlyList<TaskFeatureTarget> features) : IPlanningAgentAccess
    {
        public Task<IReadOnlyList<TaskFeatureTarget>> ListTaskFeaturesAsync(CancellationToken cancellationToken = default) => Task.FromResult(features);
        public Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<PlanningEntityState?>(
            id == Auth.FeatureId ? new(kind, id, Auth.ProjectId, Auth.MilestoneId, 1, Auth.FeatureTitle, "Вход и восстановление пароля", "new", false, 0) : null);
        public Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic evaluation never writes data.");
    }
    private sealed class Knowledge : IKnowledgeAgentAccess
    {
        public Task<IReadOnlyList<KnowledgeNodeState>> ListSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KnowledgeNodeState>>([]);
        public Task<KnowledgeNodeState?> ReadAsync(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<KnowledgeNodeState?>(null);
        public Task<KnowledgeMutationResult> ApplyAsync(KnowledgeMutation mutation, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic evaluation never writes data.");
    }
    private sealed class EmptySearch : ISearchService
    {
        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new SearchResponse([], null, true, null));
    }
}

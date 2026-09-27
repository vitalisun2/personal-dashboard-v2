using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalDashboard.V2.Agent.Application;
using PersonalDashboard.V2.Agent.Domain;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Search;
using System.Text.Json;

namespace PersonalDashboard.V2.Agent.Api;

public static class AgentApiModule
{
    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v2/agent/conversations");
        api.MapPost("/{conversationId:guid}/turns", SendTurnAsync);
        api.MapPost("/{conversationId:guid}/proposals/{proposalId:guid}/confirm", ConfirmProposalAsync);
        return endpoints;
    }

    private static async Task<IResult> SendTurnAsync(HttpContext httpContext, Guid conversationId, SendTurnRequest request,
        IChatConversationStore chats, IAgentTurnService agent, ITasksAgentAccess tasks,
        ILoggerFactory loggerFactory, IOptions<JsonOptions> jsonOptions, CancellationToken cancellationToken)
    {
        try
        {
            return await chats.ExecuteConversationAsync(conversationId, ct => SendTurnCoreAsync(httpContext, conversationId, request, chats, agent, tasks,
                loggerFactory, jsonOptions, ct), cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = "Chat session has ended." });
        }
    }

    private static async Task<IResult> SendTurnCoreAsync(HttpContext httpContext, Guid conversationId, SendTurnRequest request,
        IChatConversationStore chats, IAgentTurnService agent, ITasksAgentAccess tasks,
        ILoggerFactory loggerFactory, IOptions<JsonOptions> jsonOptions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return Results.BadRequest(new { error = "Message is required." });
        const string requestedModel = "Gemma";
        var conversation = await chats.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null) return Results.NotFound();
        if (request.Scope?.Area is { } requestedArea && requestedArea != conversation.Area)
            return Results.BadRequest(new { error = "The chat area cannot be changed during a conversation." });
        var scope = new AgentScope(request.Scope?.Mode ?? "general", request.Scope?.EntityType, request.Scope?.EntityId,
            request.Scope?.EntityVersion, conversation.Area);
        if (scope.Mode is not ("general" or "entity")) return Results.BadRequest(new { error = "Scope mode must be general or entity." });
        if (scope.Mode == "entity" && (scope.EntityType is null || !ScopeKindAllowed(scope.EntityType, conversation.Area)))
            return Results.BadRequest(new { error = "The focused entity does not belong to this chat area." });
        if (scope.Mode == "entity" && conversation.Area == "planning" && scope.EntityType == "tasks.task")
        {
            var focusedTask = await tasks.ReadAsync(TaskEntityKind.Task, scope.EntityId!.Value, cancellationToken);
            if (focusedTask?.Planning?.FeatureId is null)
                return Results.BadRequest(new { error = "Only tasks linked to a planning feature can be focused in this chat." });
        }
        var turns = await chats.GetRecentTurnsAsync(conversationId, 20, cancellationToken);
        var recent = turns.SelectMany(turn => new ModelMessage[]
        {
            new("system", $"Previous turn context: {FormatScope(turn.Scope)}; requested model {turn.RequestedModel}, actual model {turn.ActualModel}."),
            new("user", turn.UserText),
            new("assistant", FormatAssistantContext(turn))
        }).ToArray();
        var pendingDraft = await chats.GetPendingProposalAsync(conversationId, cancellationToken);
        if (pendingDraft is not null)
            recent = recent.Append(ProposalDraftContext.ToMessage(pendingDraft)).ToArray();
        var turnId = Guid.NewGuid();
        var stream = WantsNdjson(httpContext.Request);
        var streamStarted = false;
        if (stream)
        {
            httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-cache, no-transform";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";
            httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        }
        AgentTurnResult result;
        try
        {
            result = await agent.RespondAsync(new AgentTurnRequest(conversationId, turnId, request.Message.Trim(), scope,
                requestedModel, ChatModelRoute.Default, recent,
                turns.SelectMany(turn => turn.Sources).Where(source => !source.IsChatHistory)
                    .Reverse().DistinctBy(source => (source.Kind, source.Id)).Take(30).Reverse().ToArray(),
                PendingProposal: pendingDraft,
                Progress: stream ? async (stage, text, ct) =>
                {
                    if (!httpContext.Response.HasStarted) await httpContext.Response.StartAsync(ct);
                    streamStarted = true;
                    await WriteNdjsonAsync(httpContext, new { type = "progress", turnId, stage, text }, jsonOptions.Value.SerializerOptions, ct);
                } : null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ChatModelUnavailableException)
        {
            if (stream && (streamStarted || httpContext.Response.HasStarted))
            {
                await WriteNdjsonAsync(httpContext, new { type = "error", message = "Gemma временно недоступна. Попробуйте позже." }, jsonOptions.Value.SerializerOptions, httpContext.RequestAborted);
                return Results.Empty;
            }
            return Results.Json(new { error = "Gemma временно недоступна. Попробуйте позже." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception error) when (stream && (streamStarted || httpContext.Response.HasStarted) && error is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("PersonalDashboard.V2.Agent.Stream").LogError(error, "Streaming chat turn {TurnId} failed after response start.", turnId);
            await WriteNdjsonAsync(httpContext, new { type = "error", message = "Не удалось завершить ответ." }, jsonOptions.Value.SerializerOptions, httpContext.RequestAborted);
            return Results.Empty;
        }
        catch (Exception error) when (stream && error is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("PersonalDashboard.V2.Agent.Stream").LogError(error, "Chat turn {TurnId} failed before the progress stream started.", turnId);
            return Results.Json(new { error = "Не удалось обработать сообщение." }, statusCode: StatusCodes.Status500InternalServerError);
        }

        var now = DateTimeOffset.UtcNow;
        var turn = new ChatTurn(turnId, conversationId, request.Message.Trim(), result.Answer,
            new ChatTurnScope(result.Scope.Mode, result.Scope.EntityType, result.Scope.EntityId, result.Scope.EntityVersion, result.Scope.Area),
            result.RequestedModel, result.ActualModel, result.ModelRoute, result.Sources, now, result.FallbackReason);
        ChatProposal? proposal = result.Proposal is null ? null : ToContract(result.Proposal);
        try
        {
            await ProposalDraftPersistence.PersistTurnAsync(chats, turn, proposal, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (stream && (streamStarted || httpContext.Response.HasStarted))
        {
            loggerFactory.CreateLogger("PersonalDashboard.V2.Agent.Stream").LogError(error, "Could not persist completed chat turn {TurnId}.", turn.Id);
            await WriteNdjsonAsync(httpContext, new { type = "error", message = "Не удалось сохранить ответ." }, jsonOptions.Value.SerializerOptions, httpContext.RequestAborted);
            return Results.Empty;
        }

        var view = ToTurnView(turn, proposal);
        if (stream)
        {
            await WriteNdjsonAsync(httpContext, new { type = "result", turn = view }, jsonOptions.Value.SerializerOptions, httpContext.RequestAborted);
            return Results.Empty;
        }
        return Results.Ok(new { turn = view });
    }

    private static bool WantsNdjson(HttpRequest request) => request.Headers.Accept.Any(value =>
        value?.Split(',').Any(mediaType => mediaType.Split(';')[0].Trim()
            .Equals("application/x-ndjson", StringComparison.OrdinalIgnoreCase)) == true);

    private static async Task WriteNdjsonAsync(HttpContext context, object value, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Response.HasStarted) await context.Response.StartAsync(cancellationToken);
        var line = JsonSerializer.Serialize(value, serializerOptions) + "\n";
        await context.Response.WriteAsync(line, cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    private static async Task<IResult> ConfirmProposalAsync(Guid conversationId, Guid proposalId,
        IProposalConfirmationService confirmations, IChatConversationStore chats, CancellationToken cancellationToken)
    {
        var proposal = await chats.GetProposalAsync(proposalId, cancellationToken);
        if (proposal is null || proposal.ConversationId != conversationId) return Results.NotFound();
        var result = await confirmations.ConfirmAsync(proposalId, proposalId, cancellationToken);
        return result.Applied ? Results.Ok(new { result.Applied, result.AlreadyApplied })
            : Results.Conflict(new { result.Stale, result.Expired, result.Error, result.CurrentValues });
    }

    private static ChatProposal ToContract(ChangeProposal proposal)
    {
        var actions = proposal.Changes.Select(change => new ChatProposedAction(change.Id, change.Target.EntityType,
            change.Target.EntityId, change.Operation.ToString(), change.Target.ExpectedVersion,
            JsonDocument.Parse(change.AfterJson).RootElement.Clone(),
            $"{change.DisplayName}\n{change.Preview}",
            change.BeforeJson is null ? null : JsonDocument.Parse(change.BeforeJson).RootElement.Clone(),
            JsonDocument.Parse(change.AfterJson).RootElement.Clone())).ToArray();
        return new ChatProposal(proposal.Id, proposal.ConversationId, proposal.TurnId, actions,
            ChatProposalState.Pending, null, proposal.CreatedAt);
    }

    private static object ToTurnView(ChatTurn turn, ChatProposal? proposal) => new
    {
        id = turn.Id,
        userMessage = turn.UserText,
        assistantMessage = turn.AssistantText,
        scope = new { mode = turn.Scope.Mode, area = turn.Scope.Area, entityType = turn.Scope.EntityType, entityId = turn.Scope.EntityId, entityVersion = turn.Scope.EntityVersion },
        requestedModel = turn.RequestedModel,
        actualModel = turn.ActualModel,
        modelRoute = turn.ModelRoute.ToString(),
        fallbackReason = turn.FallbackReason,
        createdAt = turn.CreatedAtUtc,
        sourceReferences = turn.Sources.Select(source => source.Url ?? source.Path ?? $"{source.Kind}:{source.Id}"),
        sourceDetails = turn.Sources.Select(source => new { kind = source.Kind, title = source.Title, path = source.Path,
            url = source.Url, snippet = source.Snippet, highlight = source.Highlight,
            semanticSimilarity = source.SemanticSimilarity, matchKind = source.MatchKind?.ToString().ToLowerInvariant(),
            isShowResult = source.IsShowResult }),
        proposalId = proposal?.Id,
        proposalStatus = proposal?.State.ToString(),
        changes = proposal?.Actions.Select(action =>
        {
            var split = action.Label.Split('\n', 2);
            return new { id = action.Id, operation = action.Operation, entityType = action.EntityType, entityId = action.EntityId,
                displayName = split[0], preview = split.Length > 1 ? split[1] : split[0], before = action.Before, after = action.After };
        })
    };

    private static string FormatScope(ChatTurnScope scope) => scope.Mode == "entity"
        ? $"{scope.EntityType}/{scope.EntityId} at version {scope.EntityVersion}" : "general";

    private static string FormatAssistantContext(ChatTurn turn)
    {
        var shown = turn.Sources.Where(source => source.IsShowResult).ToArray();
        if (shown.Length == 0) return turn.AssistantText;

        var sections = new List<string>();
        foreach (var (kind, heading) in new[]
                 {
                     (SearchMatchKind.Lexical, "Прямые совпадения"),
                     (SearchMatchKind.Semantic, "По смыслу")
                 })
        {
            var sources = shown.Where(source => source.MatchKind == kind).ToArray();
            if (sources.Length == 0) continue;
            sections.Add(heading + ":\n" + string.Join("\n", sources.Select((source, index) =>
                $"{index + 1}. {source.Title}" + (string.IsNullOrWhiteSpace(source.Snippet) ? string.Empty : $" — {source.Snippet}"))));
        }
        if (!string.IsNullOrWhiteSpace(turn.AssistantText)) sections.Add(turn.AssistantText);
        return string.Join("\n\n", sections);
    }

    private static string ShortTitle(string text) => text.Length <= 72 ? text : text[..69] + "...";

    private sealed record SendTurnRequest(string Message, ScopeRequest? Scope, string? RequestedModel = null);
    private static bool ScopeKindAllowed(string kind, string area) => area switch
    {
        "knowledge" => kind is "knowledge.document" or "knowledge.section",
        "tasks" => kind is "tasks.task" or "tasks.section",
        "planning" => kind is "planning.project" or "planning.milestone" or "planning.feature" or "tasks.task",
        "general" => kind is "knowledge.document" or "knowledge.section" or "planning.project" or "planning.milestone" or "planning.feature" or "tasks.task" or "tasks.section",
        _ => false
    };

    private sealed record ScopeRequest(string Mode, string? EntityType, Guid? EntityId, long? EntityVersion, string? Area = null);
}

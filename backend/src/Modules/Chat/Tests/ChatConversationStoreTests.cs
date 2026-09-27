using System.Text.Json;
using PersonalDashboard.V2.Chat.Infrastructure;
using PersonalDashboard.V2.Contracts.Chat;
using Xunit;

namespace PersonalDashboard.V2.Chat.Tests;

public sealed class ChatConversationStoreTests
{
    [Fact]
    public async Task New_store_does_not_restore_previous_conversations()
    {
        var first = new ChatConversationStore();
        var conversationId = Guid.NewGuid();
        await first.CreateConversationAsync(conversationId, "temporary", "knowledge");
        await first.AppendTurnAsync(Turn(conversationId));

        var nextProcess = new ChatConversationStore();

        Assert.Null(await nextProcess.GetConversationAsync(conversationId));
        Assert.Empty((await nextProcess.ListConversationsAsync(null, 20)).Conversations);
    }

    [Fact]
    public async Task Delete_removes_turns_and_draft_and_id_cannot_be_reused()
    {
        var store = new ChatConversationStore();
        var conversationId = Guid.NewGuid();
        var turn = Turn(conversationId);
        await store.CreateConversationAsync(conversationId, null);
        await store.AppendTurnAsync(turn);
        await store.SaveProposalAsync(Proposal(conversationId, turn.Id));

        await store.DeleteConversationAsync(conversationId);

        Assert.Null(await store.GetConversationAsync(conversationId));
        Assert.Empty(await store.GetRecentTurnsAsync(conversationId, 20));
        Assert.Null(await store.GetPendingProposalAsync(conversationId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.AppendTurnAsync(Turn(conversationId)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SaveProposalAsync(Proposal(conversationId, turn.Id)));
    }

    [Fact]
    public async Task Delete_during_inflight_turn_prevents_late_append_from_resurrecting_chat()
    {
        var store = new ChatConversationStore();
        var conversationId = Guid.NewGuid();
        await store.CreateConversationAsync(conversationId, null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = store.ExecuteConversationAsync(conversationId, async ct =>
        {
            entered.SetResult();
            await continueWork.Task.WaitAsync(ct);
            await store.AppendTurnAsync(Turn(conversationId), ct);
            return true;
        });
        await entered.Task;

        var deletion = store.DeleteConversationAsync(conversationId);
        continueWork.SetResult();

        await Task.WhenAll(request, deletion);
        Assert.Null(await store.GetConversationAsync(conversationId));
        Assert.Empty((await store.ListConversationsAsync(null, 20)).Conversations);
    }

    [Fact]
    public async Task Idle_session_expiry_removes_transcript_and_pending_draft()
    {
        var store = new ChatConversationStore();
        var conversationId = Guid.NewGuid();
        var turn = Turn(conversationId);
        await store.CreateConversationAsync(conversationId, null, "knowledge");
        await store.AppendTurnAsync(turn);
        await store.SaveProposalAsync(Proposal(conversationId, turn.Id));

        store.ExpireIdleSessions(DateTimeOffset.UtcNow.AddMinutes(31));

        Assert.Null(await store.GetConversationAsync(conversationId));
        Assert.Null(await store.GetPendingProposalAsync(conversationId));
    }

    private static ChatTurn Turn(Guid conversationId) => new(Guid.NewGuid(), conversationId, "hello", "answer",
        new ChatTurnScope("general", null, null, null), "Gemma", "Gemma", ChatModelRoute.Default, [], DateTimeOffset.UtcNow);

    private static ChatProposal Proposal(Guid conversationId, Guid turnId)
    {
        using var document = JsonDocument.Parse("{}");
        var action = new ChatProposedAction(Guid.NewGuid(), "document", Guid.NewGuid(), "Create", null,
            document.RootElement.Clone(), "New document", null, null);
        return new ChatProposal(Guid.NewGuid(), conversationId, turnId, [action], ChatProposalState.Pending, null, DateTimeOffset.UtcNow);
    }
}

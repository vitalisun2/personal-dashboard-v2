using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Transactions;

namespace PersonalDashboard.V2.Agent.Api;

public static class ProposalDraftPersistence
{
    public static Task PersistTurnAsync(
        IChatConversationStore chats,
        ITransactionRunner transactions,
        ChatTurn turn,
        ChatProposal? replacement,
        CancellationToken cancellationToken = default) =>
        transactions.ExecuteAsync(async ct =>
        {
            await chats.AppendTurnAsync(turn, ct);
            if (replacement is null) return;
            await chats.DismissPendingProposalsAsync(turn.ConversationId, ct);
            await chats.SaveProposalAsync(replacement, ct);
        }, cancellationToken);
}

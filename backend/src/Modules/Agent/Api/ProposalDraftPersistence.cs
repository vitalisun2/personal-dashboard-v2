using PersonalDashboard.V2.Contracts.Chat;

namespace PersonalDashboard.V2.Agent.Api;

public static class ProposalDraftPersistence
{
    public static Task PersistTurnAsync(
        IChatConversationStore chats,
        ChatTurn turn,
        ChatProposal? replacement,
        CancellationToken cancellationToken = default) =>
        chats.AppendTurnWithProposalAsync(turn, replacement, cancellationToken);
}

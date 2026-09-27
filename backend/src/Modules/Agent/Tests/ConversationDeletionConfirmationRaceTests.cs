using System.Text.Json;
using PersonalDashboard.V2.Agent.Application;
using PersonalDashboard.V2.Chat.Infrastructure;
using PersonalDashboard.V2.Contracts.AgentAccess;
using PersonalDashboard.V2.Contracts.Chat;
using PersonalDashboard.V2.Contracts.Planning;
using PersonalDashboard.V2.Contracts.Transactions;
using Xunit;

namespace PersonalDashboard.V2.Agent.Tests;

public sealed class ConversationDeletionConfirmationRaceTests
{
    [Fact]
    public async Task Duplicate_confirmations_are_serialized_with_delete_and_cannot_restore_the_chat()
    {
        var store = new ChatConversationStore();
        var conversationId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        await store.CreateConversationAsync(conversationId, "Test", "tasks");
        var access = new BlockingTaskAccess();
        var proposal = TaskProposal(conversationId, turnId, access.SectionId);
        await store.SaveProposalAsync(proposal);

        var service = Service(store, new InlineTransactionRunner(), access);
        var first = service.ConfirmAsync(proposal.Id, Guid.NewGuid());
        await access.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = service.ConfirmAsync(proposal.Id, Guid.NewGuid());
        var deletion = store.DeleteConversationAsync(conversationId);
        Assert.False(second.IsCompleted);
        Assert.False(deletion.IsCompleted);
        access.ReleaseApply.TrySetResult();

        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(5));
        await deletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(firstResult.Applied);
        Assert.False(secondResult.Applied);
        Assert.Equal(1, access.ApplyCount);
        Assert.Null(await store.GetConversationAsync(conversationId));
        Assert.Null(await store.GetProposalAsync(proposal.Id));
    }

    [Fact]
    public async Task Failed_confirmation_transaction_leaves_proposal_pending()
    {
        var store = new ChatConversationStore();
        var conversationId = Guid.NewGuid();
        await store.CreateConversationAsync(conversationId, "Test", "tasks");
        var access = new BlockingTaskAccess();
        var proposal = TaskProposal(conversationId, Guid.NewGuid(), access.SectionId);
        await store.SaveProposalAsync(proposal);
        access.ReleaseApply.TrySetResult();

        var service = Service(store, new FailingTransactionRunner(), access);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(proposal.Id, Guid.NewGuid()));

        Assert.Equal(ChatProposalState.Pending, (await store.GetProposalAsync(proposal.Id))?.State);
    }

    private static ProposalConfirmationService Service(IChatConversationStore store, ITransactionRunner transactions,
        ITasksAgentAccess tasks) => new(store, transactions, new EmptyKnowledgeAccess(), new EmptyPlanningAccess(), tasks);

    private static ChatProposal TaskProposal(Guid conversationId, Guid turnId, Guid sectionId)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            title = "One task",
            description = "",
            placement = "backlog",
            sectionId,
            backlogSectionName = "Inbox",
            expectedBacklogSectionVersion = 1
        }));
        var action = new ChatProposedAction(Guid.NewGuid(), "tasks.task", Guid.NewGuid(), "Create", null,
            document.RootElement.Clone(), "One task", null, document.RootElement.Clone());
        return new ChatProposal(Guid.NewGuid(), conversationId, turnId, [action], ChatProposalState.Pending, null, DateTimeOffset.UtcNow);
    }

    private sealed class InlineTransactionRunner : ITransactionRunner
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }

    private sealed class FailingTransactionRunner : ITransactionRunner
    {
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            await operation(cancellationToken);
            throw new InvalidOperationException("Simulated transaction failure.");
        }

        public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            _ = await operation(cancellationToken);
            throw new InvalidOperationException("Simulated transaction failure.");
        }
    }

    private sealed class BlockingTaskAccess : ITasksAgentAccess
    {
        private int _applyCount;
        public TaskCompletionSource ApplyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseApply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ApplyCount => Volatile.Read(ref _applyCount);

        public Task<IReadOnlyList<TaskBacklogSection>> ListBacklogSectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskBacklogSection>>([new TaskBacklogSection(SectionId, "Inbox", 1)]);

        public Task<TaskEntityState?> ReadAsync(TaskEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<TaskEntityState?>(null);

        public async Task<TaskMutationResult> ApplyAsync(TaskMutation mutation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _applyCount);
            ApplyEntered.TrySetResult();
            await ReleaseApply.Task.WaitAsync(cancellationToken);
            return new TaskMutationResult(true, null, null);
        }

        public Guid SectionId { get; } = Guid.NewGuid();
    }

    private sealed class EmptyKnowledgeAccess : IKnowledgeAgentAccess
    {
        public Task<IReadOnlyList<KnowledgeNodeState>> ListSectionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KnowledgeNodeState>>([]);
        public Task<KnowledgeNodeState?> ReadAsync(KnowledgeNodeKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<KnowledgeNodeState?>(null);
        public Task<KnowledgeMutationResult> ApplyAsync(KnowledgeMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyPlanningAccess : IPlanningAgentAccess
    {
        public Task<IReadOnlyList<TaskFeatureTarget>> ListTaskFeaturesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskFeatureTarget>>([]);
        public Task<PlanningEntityState?> ReadAsync(PlanningEntityKind kind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<PlanningEntityState?>(null);
        public Task<PlanningMutationResult> ApplyAsync(PlanningMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

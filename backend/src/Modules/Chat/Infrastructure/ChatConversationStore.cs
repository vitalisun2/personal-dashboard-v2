using System.Text;
using PersonalDashboard.V2.Contracts.Chat;

namespace PersonalDashboard.V2.Chat.Infrastructure;

/// <summary>
/// Keeps chat history and pending proposals only in this process. Idle sessions expire after 30 minutes.
/// </summary>
public sealed class ChatConversationStore : IChatConversationStore
{
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Session> _sessions = [];

    public Task<ChatConversationState?> GetConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            SweepExpiredLocked(DateTimeOffset.UtcNow);
            return Task.FromResult(_sessions.TryGetValue(id, out var session)
                ? TouchAndGetState(session)
                : null);
        }
    }

    public Task<ChatConversationState> CreateConversationAsync(Guid id, string? title, CancellationToken cancellationToken = default) =>
        CreateConversationAsync(id, title, "general", cancellationToken);

    public Task<ChatConversationState> CreateConversationAsync(Guid id, string? title, string area, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == Guid.Empty) throw new ArgumentException("Conversation ID is required.", nameof(id));
        lock (_sync)
        {
            SweepExpiredLocked(DateTimeOffset.UtcNow);
            if (_sessions.TryGetValue(id, out var existing))
            {
                existing.LastAccessUtc = DateTimeOffset.UtcNow;
                return Task.FromResult(State(existing));
            }

            var now = DateTimeOffset.UtcNow;
            var session = new Session(id, CleanTitle(title), CleanArea(area), now, now);
            _sessions.Add(id, session);
            return Task.FromResult(State(session));
        }
    }

    public Task<ChatConversationPage> ListConversationsAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            SweepExpiredLocked(DateTimeOffset.UtcNow);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var items = _sessions.Values.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id);
            if (!string.IsNullOrEmpty(cursor))
            {
                var (updated, id) = DecodeCursor(cursor);
                items = items.Where(x => x.UpdatedAtUtc < updated || (x.UpdatedAtUtc == updated && x.Id.CompareTo(id) < 0))
                    .OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id);
            }

            var rows = items.Take(pageSize + 1).ToArray();
            var hasMore = rows.Length > pageSize;
            if (hasMore) rows = rows[..pageSize];
            var summaries = rows.Select(x => new ChatConversationSummary(x.Id, x.Title, x.UpdatedAtUtc, x.Turns.Count)).ToArray();
            var next = hasMore && rows.Length > 0 ? EncodeCursor(rows[^1].UpdatedAtUtc, rows[^1].Id) : null;
            return Task.FromResult(new ChatConversationPage(summaries, next));
        }
    }

    public async Task DeleteConversationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Session? session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out session)) return;
        }

        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (_sync)
                if (_sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session))
                    _sessions.Remove(id);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public Task<IReadOnlyList<ChatTurn>> GetRecentTurnsAsync(Guid conversationId, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var session = GetActiveLocked(conversationId);
            if (session is null) return Task.FromResult<IReadOnlyList<ChatTurn>>([]);
            limit = Math.Clamp(limit, 0, 100);
            IReadOnlyList<ChatTurn> turns = session.Turns.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
                .Take(limit).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArray();
            return Task.FromResult(turns);
        }
    }

    public Task<ChatTurnPage> GetTurnsPageAsync(Guid conversationId, string? cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var session = GetActiveLocked(conversationId);
            if (session is null) return Task.FromResult(new ChatTurnPage([], null));
            pageSize = Math.Clamp(pageSize, 1, 100);
            IEnumerable<ChatTurn> query = session.Turns;
            if (!string.IsNullOrEmpty(cursor))
            {
                var (created, id) = DecodeCursor(cursor);
                query = query.Where(x => x.CreatedAtUtc < created || (x.CreatedAtUtc == created && x.Id.CompareTo(id) < 0));
            }

            var rows = query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(pageSize + 1).ToArray();
            var hasMore = rows.Length > pageSize;
            if (hasMore) rows = rows[..pageSize];
            var next = hasMore && rows.Length > 0 ? EncodeCursor(rows[^1].CreatedAtUtc, rows[^1].Id) : null;
            Array.Reverse(rows);
            return Task.FromResult(new ChatTurnPage(rows, next));
        }
    }

    public Task AppendTurnAsync(ChatTurn turn, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var session = GetActiveLocked(turn.ConversationId) ?? throw Missing(turn.ConversationId);
            if (session.Turns.Any(x => x.Id == turn.Id)) return Task.CompletedTask;
            session.Turns.Add(turn);
            session.UpdatedAtUtc = turn.CreatedAtUtc;
            if (string.IsNullOrWhiteSpace(session.Title)) session.Title = ShortTitle(turn.UserText);
            return Task.CompletedTask;
        }
    }

    public Task AppendTurnWithProposalAsync(ChatTurn turn, ChatProposal? replacement, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var session = GetActiveLocked(turn.ConversationId) ?? throw Missing(turn.ConversationId);
            if (replacement is not null && replacement.ConversationId != turn.ConversationId)
                throw new ArgumentException("The proposal must belong to the turn conversation.", nameof(replacement));

            if (!session.Turns.Any(x => x.Id == turn.Id))
            {
                session.Turns.Add(turn);
                session.UpdatedAtUtc = turn.CreatedAtUtc;
                if (string.IsNullOrWhiteSpace(session.Title)) session.Title = ShortTitle(turn.UserText);
            }

            if (replacement is not null)
            {
                foreach (var pending in session.Proposals.Values.Where(x => x.State == ChatProposalState.Pending).ToArray())
                    session.Proposals[pending.Id] = pending with { State = ChatProposalState.Dismissed };
                session.Proposals[replacement.Id] = replacement;
            }

            return Task.CompletedTask;
        }
    }

    public Task<ChatProposal?> GetProposalAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            SweepExpiredLocked(DateTimeOffset.UtcNow);
            return Task.FromResult(_sessions.Values.SelectMany(x => x.Proposals.Values).FirstOrDefault(x => x.Id == id));
        }
    }

    public Task<ChatProposal?> GetProposalForTurnAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            SweepExpiredLocked(DateTimeOffset.UtcNow);
            return Task.FromResult(_sessions.Values.SelectMany(x => x.Proposals.Values).FirstOrDefault(x => x.TurnId == turnId));
        }
    }

    public Task<ChatProposal?> GetPendingProposalAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var session = GetActiveLocked(conversationId);
            var proposal = session?.Proposals.Values.Where(x => x.State == ChatProposalState.Pending)
                .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            return Task.FromResult(proposal);
        }
    }

    public Task SaveProposalAsync(ChatProposal proposal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var session = GetActiveLocked(proposal.ConversationId) ?? throw Missing(proposal.ConversationId);
            session.Proposals[proposal.Id] = proposal;
            return Task.CompletedTask;
        }
    }

    public Task<int> DismissPendingProposalsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var session = GetActiveLocked(conversationId) ?? throw Missing(conversationId);
            var pending = session.Proposals.Values.Where(x => x.State == ChatProposalState.Pending).ToArray();
            foreach (var proposal in pending)
                session.Proposals[proposal.Id] = proposal with { State = ChatProposalState.Dismissed };
            return Task.FromResult(pending.Length);
        }
    }

    public Task<bool> TryChangeProposalStateAsync(Guid id, ChatProposalState expectedState, ChatProposalState newState,
        Guid? confirmationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            SweepExpiredLocked(DateTimeOffset.UtcNow);
            foreach (var session in _sessions.Values)
            {
                if (!session.Proposals.TryGetValue(id, out var proposal)) continue;
                if (proposal.State != expectedState) return Task.FromResult(false);
                session.Proposals[id] = proposal with { State = newState, ConfirmationId = confirmationId };
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }

    public async Task<T> ExecuteConversationAsync<T>(Guid conversationId, Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        Session session;
        lock (_sync)
        {
            session = GetActiveLocked(conversationId) ?? throw Missing(conversationId);
        }

        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (_sync)
            {
                if (!_sessions.TryGetValue(conversationId, out var current) || !ReferenceEquals(current, session))
                    throw Missing(conversationId);
                session.LastAccessUtc = DateTimeOffset.UtcNow;
            }
            return await action(cancellationToken);
        }
        finally
        {
            session.Gate.Release();
            lock (_sync) SweepExpiredLocked(DateTimeOffset.UtcNow);
        }
    }

    private ChatConversationState TouchAndGetState(Session session)
    {
        session.LastAccessUtc = DateTimeOffset.UtcNow;
        return State(session);
    }

    private Session? GetActiveLocked(Guid id)
    {
        SweepExpiredLocked(DateTimeOffset.UtcNow);
        if (!_sessions.TryGetValue(id, out var session)) return null;
        session.LastAccessUtc = DateTimeOffset.UtcNow;
        return session;
    }

    private void SweepExpiredLocked(DateTimeOffset now)
    {
        foreach (var (id, session) in _sessions.ToArray())
        {
            if (now - session.LastAccessUtc < IdleLifetime) continue;
            if (!session.Gate.Wait(0)) continue;
            try
            {
                if (_sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session))
                    _sessions.Remove(id);
            }
            finally
            {
                session.Gate.Release();
            }
        }
    }

    internal void ExpireIdleSessions(DateTimeOffset now)
    {
        lock (_sync) SweepExpiredLocked(now);
    }

    private static ChatConversationState State(Session session) => new(session.Id, session.Title, session.CreatedAtUtc, session.Area);
    private static KeyNotFoundException Missing(Guid id) => new($"Conversation {id} does not exist.");
    private static string? CleanTitle(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim()[..Math.Min(240, title.Trim().Length)];
    private static string CleanArea(string? area) => area is "knowledge" or "tasks" or "planning" or "general"
        ? area
        : throw new ArgumentException("Unknown chat area.", nameof(area));
    private static string ShortTitle(string text) => text.Length <= 60 ? text : text[..57] + "...";
    private static string EncodeCursor(DateTimeOffset timestamp, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{timestamp.UtcTicks}|{id:D}"));

    private static (DateTimeOffset Timestamp, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return (new DateTimeOffset(long.Parse(parts[0]), TimeSpan.Zero), Guid.Parse(parts[1]));
        }
        catch (Exception error) when (error is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new ArgumentException("Invalid chat page cursor.", nameof(cursor), error);
        }
    }

    private sealed class Session(Guid id, string? title, string area, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Guid Id { get; } = id;
        public string? Title { get; set; } = title;
        public string Area { get; } = area;
        public DateTimeOffset CreatedAtUtc { get; } = createdAtUtc;
        public DateTimeOffset UpdatedAtUtc { get; set; } = updatedAtUtc;
        public DateTimeOffset LastAccessUtc { get; set; } = updatedAtUtc;
        public List<ChatTurn> Turns { get; } = [];
        public Dictionary<Guid, ChatProposal> Proposals { get; } = [];
    }
}

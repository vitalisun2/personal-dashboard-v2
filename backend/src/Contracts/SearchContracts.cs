namespace PersonalDashboard.V2.Contracts.Search;

public sealed record SearchChatContext(
    Guid ConversationId,
    Guid TurnId,
    string Mode,
    string? EntityType,
    Guid? EntityId,
    long? EntityVersion);

public sealed record SearchChatFilter(
    Guid? ConversationId = null,
    string? Mode = null,
    string? EntityType = null,
    Guid? EntityId = null,
    long? EntityVersion = null);

public sealed record SearchIndexSource(
    string Kind,
    Guid Id,
    long Version,
    string Title,
    string Body,
    string? Path,
    string? Url,
    DateTimeOffset UpdatedAtUtc,
    SearchChatContext? ChatContext = null);

public sealed record SearchSourceChange(string Kind, Guid Id, long Version, bool Deleted, SearchIndexSource? Source);

public sealed record SearchSourcePage(IReadOnlyList<SearchSourceChange> Changes, string? NextCursor, bool IsComplete);

public interface ISearchIndexer
{
    Task UpsertAsync(SearchIndexSource source, CancellationToken cancellationToken = default);

    Task DeleteAsync(string kind, Guid id, long version, CancellationToken cancellationToken = default);
}

public interface ISearchSourceFeed
{
    Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default);
}

public enum SearchCoverageMode
{
    Relevant,
    Exhaustive
}

public enum SearchMatchMode
{
    Hybrid,
    Semantic,
    Lexical
}

public sealed record SearchRequest(
    string Query,
    SearchCoverageMode Mode = SearchCoverageMode.Relevant,
    IReadOnlyList<string>? Kinds = null,
    SearchChatFilter? Context = null,
    DateTimeOffset? UpdatedAfterUtc = null,
    DateTimeOffset? UpdatedBeforeUtc = null,
    string? Cursor = null,
    int? PageSize = null,
    SearchMatchMode MatchMode = SearchMatchMode.Hybrid,
    IReadOnlyList<Guid>? AllowedTaskIds = null);

public sealed record SearchSourceReference(
    string Kind,
    Guid Id,
    long Version,
    string? Url,
    string Title,
    string? Path,
    string Snippet,
    DateTimeOffset UpdatedAtUtc,
    bool IsChatHistory,
    SearchChatContext? ChatContext,
    SearchTextRange? Highlight = null,
    double? SemanticSimilarity = null,
    SearchMatchKind? MatchKind = null,
    bool IsShowResult = false);

public sealed record SearchTextRange(int Start, int Length);

public enum SearchMatchKind
{
    Lexical,
    Semantic
}

public sealed record SearchHit(
    SearchSourceReference Source,
    double Score,
    SearchMatchKind MatchKind,
    double? SemanticSimilarity = null);

public sealed record SearchResponse(
    IReadOnlyList<SearchHit> Hits,
    string? NextCursor,
    bool IsComplete,
    string? CoverageNote);

public interface ISearchService
{
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}

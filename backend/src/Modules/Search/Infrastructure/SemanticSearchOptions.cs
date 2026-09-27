using System.Globalization;
using Microsoft.Extensions.Configuration;
using PersonalDashboard.V2.Search.Domain;

namespace PersonalDashboard.V2.Search.Infrastructure;

/// <summary>Three comparable search profiles; the embedding format is independent of reranking.</summary>
public sealed class SemanticSearchOptions
{
    public SemanticSearchOptions(IConfiguration configuration)
    {
        Profile = (configuration["V2_SEARCH_PROFILE"] ?? "baseline").Trim().ToLowerInvariant();
        if (Profile is not ("baseline" or "prompted" or "reranked"))
            throw new InvalidOperationException("V2_SEARCH_PROFILE must be baseline, prompted, or reranked.");

        MinimumSimilarity = Number(configuration, "V2_SEARCH_MIN_SEMANTIC_SIMILARITY", .25, -1, 1);
        MinimumLead = Number(configuration, "V2_SEARCH_MIN_SEMANTIC_LEAD", .04, 0, 2);
        RequireLead = bool.TryParse(configuration["V2_SEARCH_REQUIRE_SEMANTIC_LEAD"], out var lead)
            ? lead : Profile == "baseline";
        DiversifySources = bool.TryParse(configuration["V2_SEARCH_DIVERSIFY_SEMANTIC_SOURCES"], out var diversify)
            ? diversify : Profile != "baseline";
        MaximumResults = Profile == "baseline" ? 5 : Integer(configuration, "V2_SEARCH_MAX_SEMANTIC_RESULTS", 5, 1, 50);
        RerankCandidates = Integer(configuration, "V2_SEARCH_RERANK_CANDIDATES", 20, 1, 20);
        RerankMinimumSimilarity = Number(configuration, "V2_SEARCH_RERANK_MIN_SIMILARITY", .20, -1, 1);
        RerankMinimumRelevance = Number(configuration, "V2_SEARCH_RERANK_MIN_RELEVANCE", .65, 0, 1);
        RerankTimeout = TimeSpan.FromSeconds(Integer(configuration, "V2_SEARCH_RERANK_TIMEOUT_SECONDS", 45, 1, 120));
    }

    public string Profile { get; }
    public bool UseRetrievalPrompts => Profile != "baseline";
    public bool UseReranker => Profile == "reranked";
    public double MinimumSimilarity { get; }
    public double MinimumLead { get; }
    public bool RequireLead { get; }
    public bool DiversifySources { get; }
    public int MaximumResults { get; }
    public int RerankCandidates { get; }
    public double RerankMinimumSimilarity { get; }
    public double RerankMinimumRelevance { get; }
    public TimeSpan RerankTimeout { get; }

    // Raw vectors retain their previous identity, allowing a genuine baseline and safe rollbacks.
    public string EmbeddingIdentity(string model) => UseRetrievalPrompts ? $"{model}|retrieval-v1" : model;
    public string QueryInput(string query) => UseRetrievalPrompts ? $"task: search result | query: {query}" : query;
    public string DocumentInput(string text) => UseRetrievalPrompts ? $"title: none | text: {text}" : text;

    public SemanticConfidencePolicy ConfidencePolicy() =>
        new(MinimumSimilarity, RequireLead ? MinimumLead : 0, MaximumResults);

    private static double Number(IConfiguration configuration, string key, double fallback, double min, double max) =>
        double.TryParse(configuration[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static int Integer(IConfiguration configuration, string key, int fallback, int min, int max) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max) : fallback;
}

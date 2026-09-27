using PersonalDashboard.V2.Search.Domain;

namespace PersonalDashboard.V2.Search.Infrastructure;

internal sealed record SemanticSelection(IReadOnlyList<SearchCandidate> Candidates, string? CoverageNote = null);

internal sealed class SemanticCandidateSelector(SemanticSearchOptions options, OllamaSemanticReranker reranker)
{
    public async Task<SemanticSelection> SelectAsync(string query, IReadOnlyList<SearchCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var vectorResults = options.ConfidencePolicy().Select(candidates);
        if (!options.UseReranker) return new SemanticSelection(vectorResults);

        // Give the model more candidates than the final result count, including weaker vector matches.
        var rerankCandidates = candidates.Where(candidate => candidate.SemanticScore is { } score
                && double.IsFinite(score) && score >= options.RerankMinimumSimilarity)
            .GroupBy(candidate => (candidate.Source.Kind, candidate.Source.Id))
            .Select(group => group.OrderByDescending(candidate => candidate.SemanticScore)
                .ThenBy(candidate => candidate.ChunkIndex).First())
            .OrderByDescending(candidate => candidate.SemanticScore)
            .ThenBy(candidate => candidate.Source.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Source.Id)
            .Take(options.RerankCandidates).ToArray();
        var reranked = await reranker.TryRerankAsync(query, rerankCandidates, cancellationToken);
        return reranked is null
            ? new SemanticSelection(vectorResults, "Проверка релевантности Gemma недоступна; показаны результаты векторного поиска без проверки моделью.")
            : new SemanticSelection(reranked);
    }
}

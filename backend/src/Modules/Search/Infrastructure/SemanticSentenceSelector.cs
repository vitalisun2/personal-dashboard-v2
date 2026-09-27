using Microsoft.Extensions.Logging;
using PersonalDashboard.V2.Search.Domain;

namespace PersonalDashboard.V2.Search.Infrastructure;

/// <summary>Reranks sentences inside each accepted semantic chunk for precise result highlighting.</summary>
internal sealed class SemanticSentenceSelector(OllamaEmbeddingClient embedder, SemanticSearchOptions options, ILogger<SemanticSentenceSelector> logger)
{
    private const int MaximumSentencesPerSource = 24;

    public async Task<IReadOnlyList<SearchCandidate>> AddMatchesAsync(
        IReadOnlyList<SearchCandidate> candidates,
        IReadOnlyList<float> queryEmbedding,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return candidates;
        try
        {
            var sentences = candidates
                .GroupBy(candidate => (candidate.Source.Kind, candidate.Source.Id))
                .Select(group => group.OrderByDescending(candidate => candidate.SemanticScore ?? double.NegativeInfinity)
                    .ThenBy(candidate => candidate.ChunkIndex).First())
                .SelectMany(candidate => SentenceSegmenter.Split(candidate.Text)
                    .Take(MaximumSentencesPerSource)
                    .Select(range => new Sentence(candidate, range, candidate.Text.Substring(range.Start, range.Length))))
                .ToArray();
            if (sentences.Length == 0) return candidates;

            var embeddings = await embedder.EmbedDocumentsAsync(sentences.Select(sentence => sentence.Text).ToArray(), cancellationToken);
            var matches = sentences.Zip(embeddings, (sentence, embedding) => new
                {
                    Sentence = sentence,
                    Score = Cosine(queryEmbedding, embedding)
                })
                .GroupBy(row => (row.Sentence.Candidate.Source.Kind, row.Sentence.Candidate.Source.Id))
                .Select(group =>
                {
                    var ranked = group.OrderByDescending(row => row.Score).ToArray();
                    var best = ranked[0];
                    var lead = ranked.Length > 1 ? best.Score - ranked[1].Score : double.PositiveInfinity;
                    var chunkScore = best.Sentence.Candidate.SemanticScore;
                    return (best.Sentence.Candidate.Source.Kind, best.Sentence.Candidate.Source.Id,
                        best.Sentence.Candidate.ChunkIndex, best.Sentence.Range,
                        Confident: double.IsFinite(best.Score) && best.Score >= options.MinimumSimilarity
                            && lead >= options.MinimumLead && (chunkScore is null || best.Score >= chunkScore));
                })
                .ToDictionary(row => (row.Kind, row.Id, row.ChunkIndex), row => (row.Range, row.Confident));

            return candidates.Select(candidate => matches.TryGetValue(
                    (candidate.Source.Kind, candidate.Source.Id, candidate.ChunkIndex), out var range)
                    ? candidate with { SemanticSentence = range.Range, SemanticSentenceConfident = range.Confident }
                    : candidate)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Semantic sentence selection failed; returning the matched chunk without highlighting.");
            return candidates;
        }
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return double.NegativeInfinity;
        double dot = 0, leftLength = 0, rightLength = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftLength += left[index] * left[index];
            rightLength += right[index] * right[index];
        }
        return leftLength == 0 || rightLength == 0 ? double.NegativeInfinity : dot / Math.Sqrt(leftLength * rightLength);
    }

    private sealed record Sentence(SearchCandidate Candidate, TextRange Range, string Text);
}

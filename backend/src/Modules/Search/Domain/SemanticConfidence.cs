namespace PersonalDashboard.V2.Search.Domain;

/// <summary>Owns the confidence rules applied after vector candidate retrieval.</summary>
public sealed class SemanticConfidencePolicy
{
    public SemanticConfidencePolicy(double minimumSimilarity, double minimumLead, int maximumSources)
    {
        if (!double.IsFinite(minimumSimilarity) || minimumSimilarity is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumSimilarity));
        if (!double.IsFinite(minimumLead) || minimumLead is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(minimumLead));
        if (maximumSources < 1) throw new ArgumentOutOfRangeException(nameof(maximumSources));
        MinimumSimilarity = minimumSimilarity;
        MinimumLead = minimumLead;
        MaximumSources = maximumSources;
    }

    public double MinimumSimilarity { get; }
    public double MinimumLead { get; }
    public int MaximumSources { get; }

    public IReadOnlyList<SearchCandidate> Select(IEnumerable<SearchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var rows = candidates.Where(candidate => candidate.SemanticScore is { } score && double.IsFinite(score))
            .ToArray();
        var sources = rows
            .GroupBy(candidate => (candidate.Source.Kind, candidate.Source.Id))
            .Select(group => new
            {
                group.Key,
                Score = group.Max(candidate => candidate.SemanticScore!.Value)
            })
            .OrderByDescending(source => source.Score)
            .ThenBy(source => source.Key.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Key.Id)
            .ToArray();

        if (sources.Length == 0 || sources[0].Score < MinimumSimilarity) return [];
        if (sources.Length > 1 && sources[0].Score - sources[1].Score < MinimumLead) return [];

        var accepted = sources.Where(source => source.Score >= MinimumSimilarity)
            .Take(MaximumSources)
            .Select(source => source.Key)
            .ToHashSet();
        return rows.Where(candidate => accepted.Contains((candidate.Source.Kind, candidate.Source.Id))).ToArray();
    }
}

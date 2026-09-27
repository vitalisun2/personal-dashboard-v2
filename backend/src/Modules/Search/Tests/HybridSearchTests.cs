using PersonalDashboard.V2.Search.Domain;
using Xunit;

namespace PersonalDashboard.V2.Search.Tests;

public sealed class HybridSearchTests
{
    [Fact]
    public void ExhaustiveDecorQueryReturnsBothSourcesAndCollapsesDuplicateChunks()
    {
        var first = Source("Первый декор");
        var second = Source("Второй декор");
        var candidates = new[]
        {
            new SearchCandidate(first, 0, "Декор города: фонари и растения", SemanticScore: .72),
            new SearchCandidate(first, 1, "Ещё про декор города и лавочки", SemanticScore: .69),
            new SearchCandidate(second, 0, "Оформление улиц, декор города, вывески", FullTextScore: .81)
        };

        var ranked = HybridSearch.Rank(new SearchCriteria("декор города", Exhaustive: true), candidates);

        Assert.Equal(2, ranked.Count);
        Assert.Contains(ranked, hit => hit.Id == first.Id);
        Assert.Contains(ranked, hit => hit.Id == second.Id);
        Assert.Equal(2, ranked.Select(hit => hit.Id).Distinct().Count());
    }

    [Fact]
    public void SourceIsLexicalWhenAnyChunkMatchesAndSemanticOnlyWhenNoneDo()
    {
        var lexicalByFts = Source("Первая запись");
        var lexicalBySubstring = Source("Вторая запись");
        var semanticOnly = Source("Третья запись");
        var ranked = HybridSearch.Rank(new SearchCriteria("фонари", Exhaustive: true),
        [
            new SearchCandidate(lexicalByFts, 0, "Другой текст", SemanticScore: .99),
            new SearchCandidate(lexicalByFts, 1, "Содержание", FullTextScore: .2),
            new SearchCandidate(lexicalBySubstring, 0, "Здесь есть фонари", SemanticScore: .8),
            new SearchCandidate(semanticOnly, 0, "Текст про улицу", SemanticScore: .7)
        ]);

        Assert.False(ranked.Single(hit => hit.Id == lexicalByFts.Id).IsSemantic);
        Assert.False(ranked.Single(hit => hit.Id == lexicalBySubstring.Id).IsSemantic);
        Assert.True(ranked.Single(hit => hit.Id == semanticOnly.Id).IsSemantic);
    }

    [Fact]
    public void SemanticModeRanksByVectorAndMarksEveryResultSemantic()
    {
        var source = Source("Декор площади");
        const string text = "Первое предложение. Декор помогает оформить городскую площадь.";
        var sentenceStart = text.IndexOf("Декор", StringComparison.Ordinal);
        var ranked = HybridSearch.Rank(new SearchCriteria("декор", SemanticOnly: true),
        [
            new SearchCandidate(source, 0, text, SemanticScore: .8, FullTextScore: .9,
                SemanticSentence: new TextRange(sentenceStart, text.Length - sentenceStart))
        ]);

        Assert.Single(ranked);
        Assert.True(ranked[0].IsSemantic);
        Assert.Equal(.8, ranked[0].SemanticSimilarity);
        var highlight = Assert.IsType<TextRange>(ranked[0].Highlight);
        Assert.Equal("Декор помогает оформить городскую площадь.",
            ranked[0].Snippet.Substring(highlight.Start, highlight.Length));
    }

    [Fact]
    public void SentenceSegmenterKeepsRussianSentencesAndMarkdownItemsSeparate()
    {
        const string text = "Первое предложение. Второе предложение!\n- Отдельный пункт без точки";

        var sentences = SentenceSegmenter.Split(text)
            .Select(range => text.Substring(range.Start, range.Length))
            .ToArray();

        Assert.Equal(["Первое предложение.", "Второе предложение!", "Отдельный пункт без точки"], sentences);
    }

    [Fact]
    public void SemanticConfidenceAcceptsClearLeaderAndOtherSourcesAboveFloor()
    {
        var leader = Source("Архитектурная база знаний");
        var related = Source("Связанный документ");
        var weak = Source("Случайный документ");

        var selected = new SemanticConfidencePolicy(.25, .04, 5).Select([
            new SearchCandidate(leader, 0, "Главный фрагмент", SemanticScore: .316),
            new SearchCandidate(leader, 1, "Другой фрагмент", SemanticScore: .290),
            new SearchCandidate(related, 0, "Связанный фрагмент", SemanticScore: .260),
            new SearchCandidate(weak, 0, "Слабый фрагмент", SemanticScore: .240)
        ]);

        Assert.Equal(3, selected.Count);
        Assert.DoesNotContain(selected, candidate => candidate.Source.Id == weak.Id);
    }

    [Fact]
    public void SemanticConfidenceRejectsWeakOrAmbiguousLeader()
    {
        var first = Source("Первый");
        var second = Source("Второй");

        var policy = new SemanticConfidencePolicy(.25, .04, 5);
        Assert.Empty(policy.Select([
            new SearchCandidate(first, 0, "Первый", SemanticScore: .233),
            new SearchCandidate(second, 0, "Второй", SemanticScore: .231)
        ]));

        Assert.Empty(policy.Select([
            new SearchCandidate(first, 0, "Первый", SemanticScore: .310),
            new SearchCandidate(second, 0, "Второй", SemanticScore: .290)
        ]));
    }

    private static IndexedSource Source(string title)
    {
        var id = Guid.NewGuid();
        return new IndexedSource("knowledge.document", id, 1, title,
            $"/knowledge/documents/{id}", $"/knowledge/documents/{id}", DateTimeOffset.UtcNow);
    }
}

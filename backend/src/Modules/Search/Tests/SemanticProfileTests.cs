using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Search.Application;
using PersonalDashboard.V2.Search.Domain;
using PersonalDashboard.V2.Search.Infrastructure;
using Xunit;

namespace PersonalDashboard.V2.Search.Tests;

public sealed class SemanticProfileTests
{
    [Theory]
    [InlineData("baseline", "embeddinggemma", "запрос", "текст")]
    [InlineData("prompted", "embeddinggemma|retrieval-v1", "task: search result | query: запрос", "title: none | text: текст")]
    [InlineData("reranked", "embeddinggemma|retrieval-v1", "task: search result | query: запрос", "title: none | text: текст")]
    public async Task IndexDocumentsAndQueriesUseTheirOwnPromptAndVersionedIdentity(
        string profile, string identity, string expectedQuery, string expectedDocument)
    {
        var inputs = new List<string[]>();
        var configuration = Config(profile);
        var options = new SemanticSearchOptions(configuration);
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("embeddinggemma", body.RootElement.GetProperty("model").GetString());
            var input = body.RootElement.GetProperty("input").EnumerateArray().Select(value => value.GetString()!).ToArray();
            inputs.Add(input);
            return Json(new { embeddings = input.Select(_ => new[] { 1f, 0f }).ToArray() });
        }));
        var embedder = new OllamaEmbeddingClient(http, configuration, options);

        await embedder.EmbedQueryAsync("запрос");
        await embedder.EmbedDocumentsAsync(["текст"]);

        Assert.Equal(identity, embedder.EmbeddingIdentity);
        Assert.Equal(expectedQuery, Assert.Single(inputs[0]));
        Assert.Equal(expectedDocument, Assert.Single(inputs[1]));
    }

    [Fact]
    public void EmbeddingFormatChangesInvalidateExistingVectorsButRerankingDoesNot()
    {
        var baseline = new SemanticSearchOptions(Config("baseline"));
        var prompted = new SemanticSearchOptions(Config("prompted"));
        var reranked = new SemanticSearchOptions(Config("reranked"));
        Assert.Equal("embeddinggemma", baseline.EmbeddingIdentity("embeddinggemma"));
        Assert.NotEqual(baseline.EmbeddingIdentity("embeddinggemma"), prompted.EmbeddingIdentity("embeddinggemma"));
        Assert.Equal(prompted.EmbeddingIdentity("embeddinggemma"), reranked.EmbeddingIdentity("embeddinggemma"));
        Assert.NotEqual(prompted.EmbeddingIdentity("other-model"), prompted.EmbeddingIdentity("embeddinggemma"));
    }

    [Fact]
    public async Task SentenceHighlightsEmbedDocumentPromptsAgainstTheQueryVector()
    {
        var configuration = Config("prompted");
        var options = new SemanticSearchOptions(configuration);
        var inputs = new List<string>();
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            inputs.AddRange(body.RootElement.GetProperty("input").EnumerateArray().Select(value => value.GetString()!));
            return Json(new { embeddings = new[] { new[] { 0f, 1f }, new[] { 1f, 0f } } });
        }));
        var selector = new SemanticSentenceSelector(new OllamaEmbeddingClient(http, configuration, options), options,
            NullLogger<SemanticSentenceSelector>.Instance);
        var candidate = Candidate(.8, "Первая фраза. Нужный ответ.");

        var result = await selector.AddMatchesAsync([candidate], [1f, 0f], CancellationToken.None);

        Assert.Equal(["title: none | text: Первая фраза.", "title: none | text: Нужный ответ."], inputs);
        var range = Assert.IsType<TextRange>(Assert.Single(result).SemanticSentence);
        Assert.True(result[0].SemanticSentenceConfident);
        Assert.Equal("Нужный ответ.", candidate.Text.Substring(range.Start, range.Length));
    }

    [Theory]
    [InlineData(.2f, 0f, .8, false)]
    [InlineData(1f, 1f, .8, false)]
    [InlineData(.6f, .1f, .8, false)]
    public async Task WeakOrDistributedSentenceSimilarityKeepsContextWithoutHighlight(float bestX, float secondX,
        double chunkScore, bool expected)
    {
        var configuration = Config("baseline");
        var options = new SemanticSearchOptions(configuration);
        var inputs = new List<string[]>();
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var text = body.RootElement.GetProperty("input").EnumerateArray().Select(value => value.GetString()!).ToArray();
            inputs.Add(text);
            var firstY = MathF.Sqrt(MathF.Max(0, 1 - bestX * bestX));
            var secondY = MathF.Sqrt(MathF.Max(0, 1 - secondX * secondX));
            return Json(new { embeddings = new[] { new[] { bestX, firstY }, new[] { secondX, secondY } } });
        }));
        var selector = new SemanticSentenceSelector(new OllamaEmbeddingClient(http, configuration, options), options,
            NullLogger<SemanticSentenceSelector>.Instance);
        var candidate = Candidate(chunkScore, "Первое предложение. Второе предложение.");

        var result = await selector.AddMatchesAsync([candidate], [1f, 0f], CancellationToken.None);

        Assert.NotNull(result[0].SemanticSentence);
        Assert.Equal(expected, result[0].SemanticSentenceConfident);
        Assert.Equal(["Первое предложение.", "Второе предложение."], Assert.Single(inputs));
    }

    [Theory]
    [InlineData("baseline", 0)]
    [InlineData("prompted", 2)]
    [InlineData("reranked", 2)]
    public void ImprovedConfidenceKeepsTwoStrongNearEqualSources(string profile, int expected)
    {
        var options = new SemanticSearchOptions(Config(profile));
        var results = options.ConfidencePolicy().Select([Candidate(.81), Candidate(.80)]);
        Assert.Equal(expected, results.Count);
    }

    [Fact]
    public void LeadOverrideAllowsAnIndependentPromptComparisonAndBaselineKeepsFiveResults()
    {
        var prompted = new SemanticSearchOptions(Config("prompted", ("V2_SEARCH_REQUIRE_SEMANTIC_LEAD", "true")));
        Assert.Empty(prompted.ConfidencePolicy().Select([Candidate(.81), Candidate(.80)]));
        var baseline = new SemanticSearchOptions(Config("baseline", ("V2_SEARCH_MAX_SEMANTIC_RESULTS", "10")));
        Assert.Equal(5, baseline.MaximumResults);
        var noLead = new SemanticSearchOptions(Config("baseline", ("V2_SEARCH_REQUIRE_SEMANTIC_LEAD", "false")));
        Assert.Equal(2, noLead.ConfidencePolicy().Select([Candidate(.81), Candidate(.80)]).Count);
    }

    [Fact]
    public void DiversityOverrideAllowsTheSameRawCandidatePoolForPromptOnlyComparison()
    {
        Assert.False(new SemanticSearchOptions(Config("baseline")).DiversifySources);
        Assert.True(new SemanticSearchOptions(Config("prompted")).DiversifySources);
        Assert.True(new SemanticSearchOptions(Config("reranked")).DiversifySources);
        var promptsOnly = new SemanticSearchOptions(Config("prompted",
            ("V2_SEARCH_REQUIRE_SEMANTIC_LEAD", "true"), ("V2_SEARCH_DIVERSIFY_SEMANTIC_SOURCES", "false")));
        Assert.True(promptsOnly.RequireLead);
        Assert.False(promptsOnly.DiversifySources);
    }

    [Fact]
    public async Task RerankingCanRescueCandidateBelowFinalVectorFloorAndRankingKeepsItsOrder()
    {
        var configuration = Config("reranked", ("V2_SEARCH_MIN_SEMANTIC_SIMILARITY", "0.7"));
        var options = new SemanticSearchOptions(configuration);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Envelope(
            "{\"results\":[{\"id\":\"c1\",\"relevance\":0.75},{\"id\":\"c2\",\"relevance\":1.0}]}"))));
        var selector = Selector(http, configuration, options);
        var vectorLeader = Candidate(.9);
        var rescued = Candidate(.3);

        var selection = await selector.SelectAsync("запрос", [vectorLeader, rescued], CancellationToken.None);
        var ranked = HybridSearch.Rank(new SearchCriteria("запрос", SemanticOnly: true), selection.Candidates);

        Assert.Null(selection.CoverageNote);
        Assert.Equal(rescued.Source.Id, ranked[0].Id);
        Assert.Equal(.3, ranked[0].SemanticSimilarity);
        Assert.Equal(vectorLeader.Source.Id, ranked[1].Id);
    }

    [Fact]
    public void PositiveRerankerScoreRemainsVisibleEvenWithAnExplicitNegativeVectorRetrievalFloor()
    {
        var rescued = Candidate(-.1) with { RerankScore = .9 };
        var ranked = HybridSearch.Rank(new SearchCriteria("запрос", SemanticOnly: true), [rescued]);
        Assert.Equal(rescued.Source.Id, Assert.Single(ranked).Id);
        Assert.Equal(-.1, ranked[0].SemanticSimilarity);
        Assert.Empty(HybridSearch.Rank(new SearchCriteria("запрос", SemanticOnly: true), [Candidate(-.1)]));
    }

    [Fact]
    public async Task RerankerSeesDistinctCandidatesBeforeFinalResultLimitAndAcceptsAnEmptyRelevantSet()
    {
        var configuration = Config("reranked", ("V2_SEARCH_MAX_SEMANTIC_RESULTS", "1"));
        var options = new SemanticSearchOptions(configuration);
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal(2, body.RootElement.GetProperty("format").GetProperty("properties")
                .GetProperty("results").GetProperty("minItems").GetInt32());
            return Envelope("{\"results\":[{\"id\":\"c1\",\"relevance\":0},{\"id\":\"c2\",\"relevance\":0}]}");
        }));
        var first = Candidate(.9);
        var selection = await Selector(http, configuration, options).SelectAsync("запрос",
            [first, first with { ChunkIndex = 1 }, Candidate(.4)], CancellationToken.None);

        Assert.Empty(selection.Candidates);
        Assert.Null(selection.CoverageNote);
    }

    [Fact]
    public async Task RerankerReceivesReadableRussianQueryAndDocumentAfterHttpJsonDecoding()
    {
        var configuration = Config("reranked");
        var options = new SemanticSearchOptions(configuration);
        const string query = "Как восстановить забытый пароль?";
        const string document = "Для восстановления доступа отправьте запрос администратору.";
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Assert.Contains(query, content, StringComparison.Ordinal);
            Assert.Contains(document, content, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u", content, StringComparison.Ordinal);
            return Envelope("{\"results\":[{\"id\":\"c1\",\"relevance\":1}]}");
        }));
        var result = await Selector(http, configuration, options).SelectAsync(query, [Candidate(.8, document)], CancellationToken.None);
        Assert.Single(result.Candidates);
        Assert.Null(result.CoverageNote);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"results\":[]}")]
    [InlineData("{\"results\":[{\"id\":\"unknown\",\"relevance\":1},{\"id\":\"c2\",\"relevance\":0.8}]}")]
    [InlineData("{\"results\":[{\"id\":\"c1\",\"relevance\":1},{\"id\":\"c1\",\"relevance\":0.8}]}")]
    [InlineData("{\"results\":[{\"id\":\"c1\",\"relevance\":1},{\"id\":\"c2\"}]}")]
    [InlineData("{\"results\":[{\"id\":\"c1\",\"relevance\":1},{\"id\":\"c2\",\"relevance\":3}]}")]
    [InlineData("{\"results\":[null,{\"id\":\"c2\",\"relevance\":0.8}]}")]
    public async Task InvalidModelResponsesFallBackToImprovedVectorsWithCoverageNote(string content)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Envelope(content))));
        await AssertFallbackAsync(http);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("transport")]
    public async Task ModelAvailabilityFailuresFallBackWithCoverageNote(string failure)
    {
        using var http = new HttpClient(new Handler((_, _) => failure switch
        {
            "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException()),
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException())
        }));
        await AssertFallbackAsync(http);
    }

    [Fact]
    public async Task LongMalformedModelOutputFallsBackAndTheApiContractRetainsItsCoverageNote()
    {
        var configuration = Config("reranked");
        var options = new SemanticSearchOptions(configuration);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Envelope(new string('x', 100_000)))));
        var candidate = Candidate(.8);
        var selection = await Selector(http, configuration, options).SelectAsync("запрос", [candidate], CancellationToken.None);
        var service = new SearchService(new FixedStore(new SearchCandidateSet(selection.Candidates, false, selection.CoverageNote)));

        var response = await service.SearchAsync(new SearchRequest("запрос", MatchMode: SearchMatchMode.Semantic));

        Assert.Contains("без проверки моделью", response.CoverageNote);
        Assert.False(response.IsComplete);
        Assert.Equal(candidate.Source.Id, Assert.Single(response.Hits).Source.Id);
        Assert.Equal(SearchMatchKind.Semantic, response.Hits[0].MatchKind);
        Assert.Equal(.8, response.Hits[0].SemanticSimilarity);
    }

    [Fact]
    public async Task LexicalRequestUsesLexicalOnlyCandidateCriteria()
    {
        var store = new FixedStore(new SearchCandidateSet([], true, null));
        var response = await new SearchService(store).SearchAsync(new SearchRequest("exact words", MatchMode: SearchMatchMode.Lexical));

        Assert.Empty(response.Hits);
        Assert.True(store.LastCriteria!.LexicalOnly);
        Assert.False(store.LastCriteria.SemanticOnly);
    }

    [Fact]
    public async Task CallerCancellationIsNotSilentlyConvertedToFallback()
    {
        var configuration = Config("reranked");
        var options = new SemanticSearchOptions(configuration);
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException(cancellation.Token));
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Selector(http, configuration, options)
            .SelectAsync("запрос", [Candidate(.8)], cancellation.Token));
    }

    private static async Task AssertFallbackAsync(HttpClient http)
    {
        var configuration = Config("reranked");
        var options = new SemanticSearchOptions(configuration);
        var candidates = new[] { Candidate(.8), Candidate(.79) };
        var result = await Selector(http, configuration, options).SelectAsync("запрос", candidates, CancellationToken.None);
        Assert.Equal(candidates, result.Candidates);
        Assert.Contains("без проверки моделью", result.CoverageNote);
        Assert.All(result.Candidates, candidate => Assert.Null(candidate.RerankScore));
    }

    private static SemanticCandidateSelector Selector(HttpClient http, IConfiguration configuration, SemanticSearchOptions options)
        => new(options, new OllamaSemanticReranker(http, configuration, options, NullLogger<OllamaSemanticReranker>.Instance));

    private static IConfiguration Config(string profile, params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["V2_SEARCH_PROFILE"] = profile };
        foreach (var (key, value) in settings) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static SearchCandidate Candidate(double score, string text = "Текст документа")
        => new(new IndexedSource("knowledge.document", Guid.NewGuid(), 1, "Запись", "/test", "/test", DateTimeOffset.UtcNow),
            0, text, SemanticScore: score);

    private static HttpResponseMessage Envelope(string content) => Json(new { message = new { content } });
    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }

    private sealed class FixedStore(SearchCandidateSet candidates) : ISearchCandidateStore
    {
        public SearchCriteria? LastCriteria { get; private set; }
        public Task<SearchCandidateSet> FindCandidatesAsync(SearchCriteria criteria, SearchCoverageMode mode,
            CancellationToken cancellationToken = default)
        {
            LastCriteria = criteria;
            return Task.FromResult(candidates);
        }
    }
}

using System.Data.Common;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Search.Infrastructure;
using PersonalDashboard.V2.Search.Domain;
using Npgsql;
using Xunit;

namespace PersonalDashboard.V2.Search.Tests;

public sealed class SearchCatchUpIntegrationTests
{
    [PgFact]
    public async Task SemanticSqlDiversifiesBeforeLimitingAndExcludesOtherEmbeddingFormats()
    {
        var kind = $"test.semantic-diversity.{Guid.NewGuid():N}";
        var longId = Guid.NewGuid();
        var relatedId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        var requests = new ConcurrentQueue<string>();
        await using var provider = SemanticProvider("prompted", true, requests);
        await InitializeSearchSchemaAsync(provider);
        var indexer = provider.GetRequiredService<ISearchIndexer>();
        var now = DateTimeOffset.UtcNow;
        foreach (var id in new[] { longId, relatedId, staleId })
            await indexer.UpsertAsync(new SearchIndexSource(kind, id, 1, "Запись", "Содержимое записи.",
                "/test", "/test", now));

        try
        {
            await SetEmbeddingAsync(provider, kind, longId, .95, "embeddinggemma|retrieval-v1");
            await SetEmbeddingAsync(provider, kind, relatedId, .80, "embeddinggemma|retrieval-v1");
            await SetEmbeddingAsync(provider, kind, staleId, .99, "embeddinggemma");
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await db.Database.OpenConnectionAsync();
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                    INSERT INTO search_chunks (kind, source_id, source_version, chunk_index, chunk_text,
                                               searchable_text, embedding, embedding_model)
                    SELECT kind, source_id, source_version, indices.value, chunk_text, searchable_text,
                           embedding, embedding_model
                      FROM search_chunks CROSS JOIN generate_series(1, 55) AS indices(value)
                     WHERE kind = @kind AND source_id = @id AND chunk_index = 0;
                    """;
                Add(command, "kind", kind);
                Add(command, "id", longId);
                Assert.Equal(55, await command.ExecuteNonQueryAsync());
            }

            var request = new SearchRequest("поисковый запрос", Kinds: [kind], MatchMode: SearchMatchMode.Semantic);
            var diverse = await provider.GetRequiredService<ISearchService>().SearchAsync(request);
            Assert.Equal(new[] { longId, relatedId }, diverse.Hits.Select(hit => hit.Source.Id));
            Assert.DoesNotContain(diverse.Hits, hit => hit.Source.Id == staleId);
            Assert.Equal(.95, diverse.Hits[0].SemanticSimilarity!.Value, 5);
            Assert.Equal(.80, diverse.Hits[1].SemanticSimilarity!.Value, 5);

            await using var legacyCandidates = SemanticProvider("prompted", false, requests);
            var crowded = await legacyCandidates.GetRequiredService<ISearchService>().SearchAsync(request);
            Assert.Equal(longId, Assert.Single(crowded.Hits).Source.Id);
            Assert.Contains(requests, input => input == "task: search result | query: поисковый запрос");
        }
        finally
        {
            foreach (var id in new[] { longId, relatedId, staleId }) await indexer.DeleteAsync(kind, id, 1);
        }
    }

    [PgFact]
    public async Task SwitchingEmbeddingFormatRebuildsStoredVectorsAndNeverSearchesRawVectorsWithPromptedQuery()
    {
        var kind = $"test.semantic-format.{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        var requests = new ConcurrentQueue<string>();
        await using var baseline = SemanticProvider("baseline", false, requests);
        await InitializeSearchSchemaAsync(baseline);
        var indexer = baseline.GetRequiredService<ISearchIndexer>();
        await indexer.UpsertAsync(new SearchIndexSource(kind, id, 1, "Документ", "Содержимое для переиндексации.",
            "/test", "/test", DateTimeOffset.UtcNow));
        try
        {
            await SetEmbeddingAsync(baseline, kind, id, .8, "embeddinggemma");
            await using var prompted = SemanticProvider("prompted", true, requests);
            var request = new SearchRequest("поисковый запрос", Kinds: [kind], MatchMode: SearchMatchMode.Semantic);
            var before = await prompted.GetRequiredService<ISearchService>().SearchAsync(request);
            Assert.Empty(before.Hits);
            Assert.Contains("индекс ещё обрабатывает", before.CoverageNote);

            var worker = prompted.GetServices<IHostedService>().Single(service => service.GetType().Name == "SearchEmbeddingWorker");
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await WaitForEmbeddingIdentityAsync(prompted, kind, id, "embeddinggemma|retrieval-v1");
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }

            var after = await prompted.GetRequiredService<ISearchService>().SearchAsync(request);
            Assert.Equal(id, Assert.Single(after.Hits).Source.Id);
            Assert.Contains(requests, input => input.StartsWith("title: none | text: Документ", StringComparison.Ordinal));
            Assert.Contains(requests, input => input == "task: search result | query: поисковый запрос");

            // Reverting the profile likewise excludes prompted vectors until the raw index is rebuilt.
            var rawAgain = await baseline.GetRequiredService<ISearchService>().SearchAsync(request);
            Assert.Empty(rawAgain.Hits);
        }
        finally
        {
            await indexer.DeleteAsync(kind, id, 1);
        }
    }

    [PgFact]
    public async Task NaturalRussianQueryFindsBothSourcesWithoutEmbeddingsAndContextOnlyFiltersChat()
    {
        var connectionString = TestConnection();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PersonalOsV2"] = connectionString,
            ["OLLAMA_URL"] = "http://127.0.0.1:11431",
            ["OLLAMA_EMBED_MODEL"] = "embeddinggemma"
        }).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddPersonalOsV2Platform(configuration);
        collection.AddSearchInfrastructure(configuration);
        collection.AddSingleton<ISearchSourceFeed>(new EmptySearchSourceFeed());
        await using var provider = collection.BuildServiceProvider();
        await InitializeSearchSchemaAsync(provider);
        var indexer = provider.GetRequiredService<ISearchIndexer>();
        var now = DateTimeOffset.UtcNow;
        var sources = new[]
        {
            new SearchIndexSource("knowledge.document", firstId, 1, "Декор площади", "Фонари на площади",
                "/test/decor-one", "/test/decor-one", now),
            new SearchIndexSource("knowledge.document", secondId, 1, "Идеи оформления", "Городской декор и лавочки",
                "/test/decor-two", "/test/decor-two", now)
        };

        try
        {
            foreach (var source in sources) await indexer.UpsertAsync(source);
            Assert.Equal(new[] { "писал", "декор" }, SearchTerms.Significant("где я писал всё про декор"));
            var result = await provider.GetRequiredService<ISearchService>().SearchAsync(new SearchRequest(
                "где я писал всё про декор", SearchCoverageMode.Exhaustive,
                ["knowledge.document"], new SearchChatFilter(EntityType: "knowledge.document", EntityId: Guid.NewGuid())));
            Assert.Contains(result.Hits, hit => hit.Source.Id == firstId);
            Assert.Contains(result.Hits, hit => hit.Source.Id == secondId);
            Assert.All(result.Hits, hit => Assert.False(hit.Source.IsChatHistory));
            Assert.All(result.Hits.Where(hit => hit.Source.Id == firstId || hit.Source.Id == secondId),
                hit => Assert.Equal(SearchMatchKind.Lexical, hit.MatchKind));

            var relevant = await provider.GetRequiredService<ISearchService>().SearchAsync(new SearchRequest(
                "где я писал всё про декор", SearchCoverageMode.Relevant,
                ["knowledge.document"], new SearchChatFilter(EntityType: "knowledge.document", EntityId: Guid.NewGuid())));
            Assert.False(relevant.IsComplete);
            Assert.Contains("1000", relevant.CoverageNote, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var source in sources) await indexer.DeleteAsync(source.Kind, source.Id, source.Version);
        }
    }

    [PgFact]
    public async Task FailedFeedPassKeepsCursorThenReconcilesAndVersionedTombstoneBlocksResurrection()
    {
        var connectionString = TestConnection();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var sourceId = Guid.NewGuid();
        const string kind = "test.search-journal";
        var source = new SearchIndexSource(kind, sourceId, 7, "Journal indexed source",
            "cursor reconciliation token", "/test/journal", "/test/journal", DateTimeOffset.UtcNow);
        var feed = new FailOnceFeed(new SearchSourceChange(kind, sourceId, 7, false, source));
        var journal = new OneChangeJournal(new EntityChange(1,
            new EntitySnapshot(kind, sourceId, 7, false, null)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PersonalOsV2"] = connectionString,
            ["OLLAMA_URL"] = "http://127.0.0.1:11431",
            ["OLLAMA_EMBED_MODEL"] = "embeddinggemma"
        }).Build();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddPersonalOsV2Platform(configuration);
        collection.AddSearchInfrastructure(configuration);
        collection.AddSingleton<ISearchSourceFeed>(feed);
        collection.RemoveAll<IEntityChangeJournal>();
        collection.AddSingleton<IEntityChangeJournal>(journal);
        await using var provider = collection.BuildServiceProvider();
        await InitializeSearchSchemaAsync(provider);
        await ResetTestRowsAsync(provider, kind, sourceId);

        var catchUp = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name == "SearchJournalCatchUpWorker");
        await catchUp.StartAsync(CancellationToken.None);
        try
        {
            await feed.RetryAttemptEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await ReadCursorAsync(provider));
            feed.AllowSuccessfulPage.TrySetResult();
            await WaitForCursorAsync(provider, 1, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await catchUp.StopAsync(CancellationToken.None);
        }

        var indexer = provider.GetRequiredService<ISearchIndexer>();
        await indexer.DeleteAsync(kind, sourceId, 7);
        await indexer.UpsertAsync(source);
        await indexer.UpsertAsync(source with { Version = 6, Title = "Stale source" });
        Assert.Equal((7L, true, 0), await ReadSourceStateAsync(provider, kind, sourceId));

        await indexer.UpsertAsync(source with { Version = 8, Title = "Recovered source", Path = "/test/recovered" });
        Assert.Equal((8L, false, 1), await ReadSourceStateAsync(provider, kind, sourceId));
    }

    private static async Task InitializeSearchSchemaAsync(IServiceProvider provider)
    {
        var initializer = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name == "SearchSchemaInitializer");
        await initializer.StartAsync(CancellationToken.None);
    }

    private static string TestConnection()
    {
        var connection = Environment.GetEnvironmentVariable("V2_SEARCH_TEST_CONNECTION")!;
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (builder.Database is null || !builder.Database.Contains("test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(builder.Database, "personal_os_v2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Search integration tests require an isolated database with 'test' in its name.");
        return connection;
    }

    private static ServiceProvider SemanticProvider(string profile, bool diversify, ConcurrentQueue<string> requests)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PersonalOsV2"] = TestConnection(),
            ["OLLAMA_URL"] = "http://test-embedding.invalid",
            ["OLLAMA_EMBED_MODEL"] = "embeddinggemma",
            ["V2_SEARCH_PROFILE"] = profile,
            ["V2_SEARCH_DIVERSIFY_SEMANTIC_SOURCES"] = diversify.ToString()
        }).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        collection.AddPersonalOsV2Platform(configuration);
        collection.AddSearchInfrastructure(configuration);
        collection.AddHttpClient<OllamaEmbeddingClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new FixtureEmbeddingHandler(requests));
        collection.AddSingleton<ISearchSourceFeed>(new EmptySearchSourceFeed());
        return collection.BuildServiceProvider();
    }

    private static async Task SetEmbeddingAsync(IServiceProvider provider, string kind, Guid id, double cosine, string identity)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            UPDATE search_chunks SET embedding = CAST(@vector AS vector), embedding_model = @identity
             WHERE kind = @kind AND source_id = @id;
            """;
        var vector = new float[768];
        vector[0] = (float)cosine;
        vector[1] = (float)Math.Sqrt(1 - cosine * cosine);
        Add(command, "vector", OllamaEmbeddingClient.ToVectorLiteral(vector));
        Add(command, "identity", identity);
        Add(command, "kind", kind);
        Add(command, "id", id);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task WaitForEmbeddingIdentityAsync(IServiceProvider provider, string kind, Guid id, string identity)
    {
        var stopAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < stopAt)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT embedding_model FROM search_chunks WHERE kind = @kind AND source_id = @id AND chunk_index = 0";
            Add(command, "kind", kind);
            Add(command, "id", id);
            if (string.Equals(await command.ExecuteScalarAsync() as string, identity, StringComparison.Ordinal)) return;
            await Task.Delay(50);
        }
        Assert.Fail("The isolated test vector was not rebuilt using the requested embedding format.");
    }

    private sealed class FixtureEmbeddingHandler(ConcurrentQueue<string> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/embed", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var inputs = body.RootElement.GetProperty("input").EnumerateArray().Select(input => input.GetString()!).ToArray();
            foreach (var input in inputs) requests.Enqueue(input);
            var vector = new float[768];
            vector[0] = 1;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { embeddings = inputs.Select(_ => vector).ToArray() }),
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task ResetTestRowsAsync(IServiceProvider provider, string kind, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var delete = db.Database.GetDbConnection().CreateCommand();
        delete.CommandText = "DELETE FROM search_sources WHERE kind = @kind AND id = @id; UPDATE search_catchup_cursor SET sequence = 0 WHERE id = 1;";
        Add(delete, "kind", kind);
        Add(delete, "id", id);
        await delete.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadCursorAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sequence FROM search_catchup_cursor WHERE id = 1;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task WaitForCursorAsync(IServiceProvider provider, long expected, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (await ReadCursorAsync(provider) == expected) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Search cursor did not reach {expected} within {timeout}.");
    }

    private static async Task<(long Version, bool Deleted, int Chunks)> ReadSourceStateAsync(
        IServiceProvider provider, string kind, Guid id)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT source.version, source.is_deleted,
                   (SELECT count(*)::integer FROM search_chunks AS chunk WHERE chunk.kind = source.kind AND chunk.source_id = source.id)
              FROM search_sources AS source WHERE source.kind = @kind AND source.id = @id;
            """;
        Add(command, "kind", kind);
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetBoolean(1), reader.GetInt32(2));
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class FailOnceFeed(SearchSourceChange recovered) : ISearchSourceFeed
    {
        private int _calls;
        public TaskCompletionSource RetryAttemptEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowSuccessfulPage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return new SearchSourcePage([recovered with { Source = null }], null, true);
            RetryAttemptEntered.TrySetResult();
            await AllowSuccessfulPage.Task.WaitAsync(cancellationToken);
            return new SearchSourcePage([recovered], null, true);
        }
    }

    private sealed class OneChangeJournal(EntityChange change) : IEntityChangeJournal
    {
        public Task<long> AppendAsync(EntitySnapshot snapshot, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EntityChangePage> ReadAfterAsync(long sequence, int pageSize, CancellationToken cancellationToken = default)
            => Task.FromResult(sequence < change.Sequence
                ? new EntityChangePage([change], change.Sequence, true)
                : new EntityChangePage([], sequence, true));
    }

    private sealed class EmptySearchSourceFeed : ISearchSourceFeed
    {
        public Task<SearchSourcePage> ReadPageAsync(string? cursor, int pageSize, CancellationToken cancellationToken = default)
            => Task.FromResult(new SearchSourcePage([], null, true));
    }

    private sealed class PgFactAttribute : FactAttribute
    {
        public PgFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("V2_SEARCH_TEST_CONNECTION")))
                Skip = "Set V2_SEARCH_TEST_CONNECTION to an isolated pgvector test database.";
        }
    }
}

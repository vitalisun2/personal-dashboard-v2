using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using PersonalDashboard.V2.Contracts.Search;
using PersonalDashboard.V2.Search.Application;
using PersonalDashboard.V2.Search.Domain;

namespace PersonalDashboard.V2.Search.Infrastructure;

public static class SearchInfrastructureModule
{
    public static IServiceCollection AddSearchInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<OllamaEmbeddingClient>(client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton(new SemanticConfidencePolicy(
            ConfiguredDouble(configuration["V2_SEARCH_MIN_SEMANTIC_SIMILARITY"], 0.25, -1, 1),
            ConfiguredDouble(configuration["V2_SEARCH_MIN_SEMANTIC_LEAD"], 0.04, 0, 2),
            maximumSources: 5));
        services.AddScoped<SemanticSentenceSelector>();
        services.AddScoped<ISearchIndexer, PostgresSearchIndexer>();
        services.AddScoped<ISearchCandidateStore, PostgresSearchCandidateStore>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<SearchIndexRebuilder>();
        services.AddHostedService<SearchSchemaInitializer>();
        services.AddHostedService<SearchEmbeddingWorker>();
        services.AddHostedService<SearchJournalCatchUpWorker>();
        return services;
    }

    private static double ConfiguredDouble(string? configured, double fallback, double minimum, double maximum) =>
        double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
}

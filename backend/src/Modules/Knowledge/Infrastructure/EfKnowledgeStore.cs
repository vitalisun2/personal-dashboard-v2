using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using System.Text.RegularExpressions;
using PersonalDashboard.V2.Contracts.Transactions;
using PersonalDashboard.V2.Knowledge.Application;
using PersonalDashboard.V2.Knowledge.Domain;
using PersonalDashboard.V2.Platform;

namespace PersonalDashboard.V2.Knowledge.Infrastructure;

internal sealed class EfKnowledgeStore(PlatformDbContext dbContext, ITransactionRunner transactions) : IKnowledgeStore
{
    public Task<TResult> InTransactionAsync<TResult>(Func<IKnowledgeTransaction, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) =>
        ExecuteAsync(operation, cancellationToken);

    private async Task<TResult> ExecuteAsync<TResult>(Func<IKnowledgeTransaction, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await transactions.ExecuteAsync(ct => operation(new KnowledgeTransaction(dbContext), ct), cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var id = exception.Entries.Select(entry => entry.Entity).OfType<KnowledgeNode>().FirstOrDefault()?.Id ?? Guid.Empty;
            throw new KnowledgeConcurrentWriteException(id);
        }
    }

    private sealed class KnowledgeTransaction(PlatformDbContext dbContext) : IKnowledgeTransaction
    {
        public async Task<IReadOnlyList<KnowledgeNode>> GetLiveNodesAsync(CancellationToken cancellationToken) =>
            await dbContext.Set<KnowledgeNode>()
                .Where(node => node.DeletedAt == null && node.ArchivedAt == null)
                .ToListAsync(cancellationToken);

        public async Task<IReadOnlyList<KnowledgeNode>> SearchLiveDocumentsAsync(string term, CancellationToken cancellationToken)
        {
            var query = dbContext.Set<KnowledgeNode>()
                .AsNoTracking()
                .Where(node => node.DeletedAt == null && node.ArchivedAt == null &&
                    node.Type == KnowledgeNodeType.Document);
            if (IsSingleWord(term))
            {
                var wordPattern = $@"\m{Regex.Escape(term)}\M";
                query = query.Where(node =>
                    Regex.IsMatch(node.Title, wordPattern, RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(node.Markdown, wordPattern, RegexOptions.IgnoreCase) ||
                    EF.Property<NpgsqlTsVector>(node, "SearchVector")
                        .Matches(EF.Functions.PlainToTsQuery("russian", term)));
            }
            else
            {
                var pattern = LiteralPattern(term);
                query = query.Where(node =>
                    EF.Functions.ILike(node.Title, pattern, "\\") || EF.Functions.ILike(node.Markdown, pattern, "\\"));
            }
            return await query
                .OrderByDescending(node =>
                    Regex.IsMatch(node.Title, $@"\m{Regex.Escape(term)}\M", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(node.Markdown, $@"\m{Regex.Escape(term)}\M", RegexOptions.IgnoreCase))
                .ThenBy(node => node.Title)
                .ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<KnowledgeNode>> GetLiveNodesByIdsAsync(IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken)
        {
            if (ids.Count == 0) return [];
            return await dbContext.Set<KnowledgeNode>()
                .AsNoTracking()
                .Where(node => ids.Contains(node.Id) && node.DeletedAt == null && node.ArchivedAt == null)
                .ToListAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<KnowledgeNode>> GetAllNodesAsync(CancellationToken cancellationToken) =>
            await dbContext.Set<KnowledgeNode>().ToListAsync(cancellationToken);

        public async Task AddAsync(KnowledgeNode node, CancellationToken cancellationToken) =>
            await dbContext.Set<KnowledgeNode>().AddAsync(node, cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);

        private static string LiteralPattern(string term) =>
            "%" + term.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal) + "%";

        private static bool IsSingleWord(string term) => term.All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}

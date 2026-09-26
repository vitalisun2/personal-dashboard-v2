using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Knowledge.Application;
using PersonalDashboard.V2.Tasks.Application;
using System.Net.Http.Json;

internal static class V1PeerSyncPolicy
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    internal static async Task PollAsync(
        HttpClient client,
        long cursor,
        Func<EntityChange, CancellationToken, Task> apply,
        Func<long, CancellationToken, Task> advanceCursor,
        CancellationToken cancellationToken)
    {
        var page = await client.GetFromJsonAsync<EntityChangePage>(
            $"/api/v2/sync/changes?after={cursor}&pageSize=100", JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("V2 changes endpoint returned an empty response.");
        await ProcessChangesAsync(page.Changes, apply, advanceCursor, cancellationToken);
    }

    internal static bool IsV1CompatibleTask(string placement, Guid? projectId) =>
        projectId is null && placement is "backlog" or "today";

    internal static string ResolveSectionName(Guid? sectionId, IReadOnlyCollection<TaskSectionView> sections)
    {
        if (sectionId is null) throw new InvalidDataException("A standalone task without a V2 section cannot be mapped to a real V1 section name.");
        return sections.SingleOrDefault(section => section.Id == sectionId)?.Name
            ?? throw new InvalidDataException($"Task section {sectionId} was not found.");
    }

    internal static IReadOnlyList<KnowledgeNodeView> MissingAncestorsRootFirst(
        Guid nodeId,
        Guid? parentId,
        IReadOnlyCollection<KnowledgeNodeView> v2Nodes,
        IReadOnlyDictionary<Guid, string> v1Nodes)
    {
        var byId = v2Nodes.Where(node => !node.Deleted).ToDictionary(node => node.Id);
        var missing = new List<KnowledgeNodeView>();
        var current = parentId;
        while (current is { } id && !v1Nodes.ContainsKey(id))
        {
            if (id == nodeId || !byId.TryGetValue(id, out var ancestor) || ancestor.Kind != "section" || ancestor.Archived)
                throw new InvalidDataException($"Knowledge parent {id} cannot be represented in V1.");
            missing.Add(ancestor);
            current = ancestor.ParentId;
        }
        if (current is { } existingParent && v1Nodes.GetValueOrDefault(existingParent) != "section")
            throw new InvalidDataException($"Knowledge parent {existingParent} is not a V1 section.");
        return missing.AsEnumerable().Reverse().ToArray();
    }

    internal static async Task ProcessChangesAsync(
        IReadOnlyList<EntityChange> changes,
        Func<EntityChange, CancellationToken, Task> apply,
        Func<long, CancellationToken, Task> advanceCursor,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            if (!change.ImportedFromPeer)
                await apply(change, cancellationToken);
            await advanceCursor(change.Sequence, cancellationToken);
        }
    }
}

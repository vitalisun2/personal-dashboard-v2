using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Knowledge.Application;
using PersonalDashboard.V2.Platform;
using PersonalDashboard.V2.Platform.Persistence;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;

internal sealed class V1PeerSyncWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<V1PeerSyncWorker> logger) : BackgroundService
{
    private readonly string? _baseUrl = configuration["V1_PEER_URL"];
    private readonly string? _key = configuration["V1_V2_SYNC_KEY"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_key)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("V1 sync poll failed ({ErrorType}); will retry.", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_key)) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var cursorRow = await db.V1PeerCursors.SingleAsync(row => row.Id == 1, ct);
        var journal = scope.ServiceProvider.GetRequiredService<IEntityChangeJournal>();
        var client = clients.CreateClient("V1Peer");
        client.BaseAddress = new Uri(_baseUrl, UriKind.Absolute);
        client.DefaultRequestHeaders.Remove("X-PersonalDashboard-Sync-Key");
        client.DefaultRequestHeaders.Add("X-PersonalDashboard-Sync-Key", _key);

        var page = await journal.ReadAfterAsync(cursorRow.Sequence, 100, ct);
        await V1PeerSyncPolicy.ProcessChangesAsync(page.Changes,
            (change, token) => ApplyChangeAsync(change, client, scope.ServiceProvider, token),
            async (sequence, token) =>
            {
                cursorRow.Sequence = sequence;
                await db.SaveChangesAsync(token);
            }, ct);
    }

    private static async Task ApplyChangeAsync(EntityChange change, HttpClient client, IServiceProvider services, CancellationToken ct)
    {
        switch (change.Snapshot.Type)
        {
            case "tasks.task":
                await ApplyTaskAsync(change.Snapshot, client, services.GetRequiredService<TasksService>(), ct);
                break;
            case "tasks.section":
                await ApplySectionAsync(change.Snapshot, client, services.GetRequiredService<TasksService>(), ct);
                break;
            case "knowledge.node":
                await ApplyKnowledgeAsync(change.Snapshot, client, services.GetRequiredService<KnowledgeService>(), ct);
                break;
        }
    }

    private static async Task ApplyTaskAsync(EntitySnapshot snapshot, HttpClient client, TasksService tasks, CancellationToken ct)
    {
        if (snapshot.Deleted)
        {
            await DeleteAsync(client, $"/api/sync/v2/tasks/{snapshot.Id}", ct);
            return;
        }
        var payload = RequiredObject(snapshot.Payload);
        var placement = ReadString(payload, "placement");
        var projectId = ReadGuid(payload, "projectId");
        if (!V1PeerSyncPolicy.IsV1CompatibleTask(placement, projectId))
        {
            // V1 cannot represent Planned/Archived or planning-linked tasks. Remove an older compatible copy.
            await DeleteAsync(client, $"/api/sync/v2/tasks/{snapshot.Id}", ct);
            return;
        }
        var sectionId = ReadGuid(payload, "sectionId");
        var sectionName = await ResolveSectionNameAsync(tasks, placement, sectionId, ct);
        await PostAsync(client, "/api/sync/v2/tasks", new
        {
            id = snapshot.Id,
            title = ReadString(payload, "title"),
            description = ReadString(payload, "body"),
            placement,
            workStatus = ReadString(payload, "workStatus"),
            sectionName,
            createdAt = ReadDate(payload, "createdAtUtc")
        }, ct);
    }

    private static async Task ApplySectionAsync(EntitySnapshot snapshot, HttpClient client, TasksService tasks, CancellationToken ct)
    {
        if (snapshot.Deleted) return;
        var payload = RequiredObject(snapshot.Payload);
        var bucket = ReadString(payload, "bucket");
        var location = bucket == "today" ? TaskLocation.Today : bucket == "backlog" ? TaskLocation.Backlog : (TaskLocation?)null;
        if (location is null) return;
        foreach (var task in await tasks.ListAsync(location, ct))
        {
            if (task.SectionId == snapshot.Id && task.ProjectId is null)
                await ApplyTaskAsync(await SnapshotAsync(tasks, task.Id, ct), client, tasks, ct);
        }
    }

    private static async Task<EntitySnapshot> SnapshotAsync(TasksService tasks, Guid id, CancellationToken ct)
    {
        var task = await tasks.GetAsync(id, ct) ?? throw new InvalidDataException($"Task {id} disappeared while syncing its section.");
        var payload = JsonSerializer.SerializeToElement(new
        {
            title = task.Title, body = task.Description, projectId = task.ProjectId, placement = task.Location.ToString().ToLowerInvariant(),
            workStatus = task.WorkStatus.ToString().ToLowerInvariant(), sectionId = task.SectionId, createdAtUtc = (DateTimeOffset?)null
        }, JsonOptions);
        return new EntitySnapshot("tasks.task", id, task.Version, false, payload);
    }

    private static async Task ApplyKnowledgeAsync(EntitySnapshot snapshot, HttpClient client, KnowledgeService knowledge, CancellationToken ct)
    {
        if (snapshot.Deleted)
        {
            await DeleteAsync(client, $"/api/sync/v2/knowledge/{snapshot.Id}", ct);
            return;
        }
        var payload = RequiredObject(snapshot.Payload);
        if (ReadBool(payload, "archived"))
        {
            await DeleteAsync(client, $"/api/sync/v2/knowledge/{snapshot.Id}", ct);
            return;
        }
        await EnsureKnowledgeAncestorsAsync(snapshot.Id, ReadGuid(payload, "parentId"), client, knowledge, ct);
        await PostAsync(client, "/api/sync/v2/knowledge", new
        {
            id = snapshot.Id,
            kind = ReadString(payload, "kind"),
            title = ReadString(payload, "title"),
            parentId = ReadGuid(payload, "parentId"),
            position = ReadInt(payload, "position"),
            markdown = ReadString(payload, "markdown"),
            createdAt = ReadDate(payload, "updatedAt"),
            updatedAt = ReadDate(payload, "updatedAt")
        }, ct);
    }

    private static async Task EnsureKnowledgeAncestorsAsync(Guid nodeId, Guid? parentId, HttpClient client, KnowledgeService knowledge, CancellationToken ct)
    {
        if (parentId is null) return;
        using var treeResponse = await client.GetAsync("/api/knowledge/tree", ct);
        treeResponse.EnsureSuccessStatusCode();
        using var tree = await JsonDocument.ParseAsync(await treeResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var present = new Dictionary<Guid, string>();
        CollectKinds(tree.RootElement, present);

        var nodes = await knowledge.GetTreeAsync(ct);
        var missing = V1PeerSyncPolicy.MissingAncestorsRootFirst(nodeId, parentId, nodes, present);
        foreach (var ancestor in missing)
        {
            await PostAsync(client, "/api/sync/v2/knowledge", new
            {
                id = ancestor.Id,
                kind = ancestor.Kind,
                title = ancestor.Title,
                parentId = ancestor.ParentId,
                position = ancestor.Position,
                markdown = ancestor.Markdown,
                createdAt = ancestor.UpdatedAt,
                updatedAt = ancestor.UpdatedAt
            }, ct);
        }
    }

    private static void CollectKinds(JsonElement element, Dictionary<Guid, string> nodes)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) CollectKinds(child, nodes);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;
        if (element.TryGetProperty("id", out var idElement) && idElement.TryGetGuid(out var id))
            nodes[id] = element.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "" : "";
        if (element.TryGetProperty("children", out var children)) CollectKinds(children, nodes);
    }

    private static async Task<string> ResolveSectionNameAsync(TasksService tasks, string placement, Guid? sectionId, CancellationToken ct)
    {
        var location = placement == "today" ? TaskLocation.Today : TaskLocation.Backlog;
        return V1PeerSyncPolicy.ResolveSectionName(sectionId, await tasks.SectionsAsync(location, ct));
    }

    private static async Task PostAsync(HttpClient client, string path, object body, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, body, ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task DeleteAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.DeleteAsync(path, ct);
        response.EnsureSuccessStatusCode();
    }

    private static JsonElement RequiredObject(JsonElement? value) => value is { ValueKind: JsonValueKind.Object } json
        ? json : throw new InvalidDataException("Entity change has no compatible payload.");
    private static string ReadString(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static Guid? ReadGuid(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) ? id : null;
    private static int ReadInt(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static DateTimeOffset? ReadDate(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result) ? result : null;
    private static bool ReadBool(JsonElement json, string name) => json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

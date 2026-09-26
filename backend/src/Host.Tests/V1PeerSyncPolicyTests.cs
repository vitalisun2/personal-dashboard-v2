using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Knowledge.Application;
using PersonalDashboard.V2.Tasks.Application;
using PersonalDashboard.V2.Tasks.Domain;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Xunit;

public sealed class V1PeerSyncPolicyTests
{
    [Fact]
    public void PeerOriginRequiresTheConfiguredSharedKey()
    {
        Assert.True(SyncEndpoints.IsTrustedPeerKey("shared-secret", "shared-secret"));
        Assert.False(SyncEndpoints.IsTrustedPeerKey("shared-secret", "wrong-secret"));
        Assert.False(SyncEndpoints.IsTrustedPeerKey("", "shared-secret"));
        Assert.False(SyncEndpoints.IsTrustedPeerKey("shared-secret", null));
    }

    [Fact]
    public async Task HttpChangeFeedAndPeerApplyPersistCursorOnlyAfterSuccessfulCompatibleTaskAndKnowledgeDelivery()
    {
        var cursorPath = Path.Combine(Path.GetTempPath(), "v2-peer-cursor-" + Guid.NewGuid() + ".txt");
        var taskId = Guid.NewGuid();
        var knowledgeId = Guid.NewGuid();
        var changes = new[]
        {
            Change(1, "tasks.task", taskId, false, new { title = "Task", placement = "backlog", projectId = (Guid?)null }),
            Change(2, "knowledge.node", knowledgeId, false, new { title = "Note", kind = "document", markdown = "body" }),
            Change(3, "tasks.task", taskId, false, new { title = "Task updated", placement = "today", projectId = (Guid?)null }),
            Change(4, "knowledge.node", knowledgeId, false, new { title = "Note updated", kind = "document", markdown = "edited" }),
            Change<object>(5, "tasks.task", taskId, true, null),
            Change<object>(6, "knowledge.node", knowledgeId, true, null),
            new EntityChange(7, new EntitySnapshot("knowledge.node", Guid.NewGuid(), 1, false, null), ImportedFromPeer: true)
        };
        var handler = new PeerHttpHandler(changes) { FailSequence = 3 };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://peer.test") };
        client.DefaultRequestHeaders.Add("X-PersonalDashboard-Sync-Key", "integration-key");
        async Task<long> ReadCursor() => File.Exists(cursorPath) ? long.Parse(await File.ReadAllTextAsync(cursorPath)) : 0;
        async Task Apply(EntityChange change, CancellationToken ct)
        {
            var route = change.Snapshot.Type == "tasks.task" ? "tasks" : "knowledge";
            var itemPath = $"/api/sync/v2/{route}/{change.Snapshot.Id}";
            if (change.Snapshot.Deleted)
            {
                using var delete = await client.DeleteAsync(itemPath, ct);
                delete.EnsureSuccessStatusCode();
            }
            else
            {
                var payload = change.Snapshot.Payload ?? JsonSerializer.SerializeToElement(new { id = change.Snapshot.Id });
                using var post = await client.PostAsJsonAsync($"/api/sync/v2/{route}", payload, ct);
                post.EnsureSuccessStatusCode();
            }
        }
        async Task Advance(long sequence, CancellationToken _) => await File.WriteAllTextAsync(cursorPath, sequence.ToString());

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(async () => await V1PeerSyncPolicy.PollAsync(client, await ReadCursor(), Apply, Advance, CancellationToken.None));
            Assert.Equal(2, await ReadCursor());
            await V1PeerSyncPolicy.PollAsync(client, await ReadCursor(), Apply, Advance, CancellationToken.None);
            Assert.Equal(7, await ReadCursor());
            Assert.Equal(new long[] { 1, 2, 3, 3, 4, 5, 6 }, handler.AppliedSequences);
            Assert.All(handler.CapturedRequests, request => Assert.Equal("integration-key", request.Key));
            Assert.Contains(handler.CapturedRequests, request => request.Method == "POST" && request.Path == "/api/sync/v2/tasks" && request.Body!.Contains("Task updated"));
            Assert.Contains(handler.CapturedRequests, request => request.Method == "POST" && request.Path == "/api/sync/v2/knowledge" && request.Body!.Contains("edited"));
            Assert.Contains(handler.CapturedRequests, request => request.Method == "DELETE" && request.Path == $"/api/sync/v2/tasks/{taskId}");
            Assert.Contains(handler.CapturedRequests, request => request.Method == "DELETE" && request.Path == $"/api/sync/v2/knowledge/{knowledgeId}");
        }
        finally { if (File.Exists(cursorPath)) File.Delete(cursorPath); }
    }

    private static EntityChange Change<T>(long sequence, string type, Guid id, bool deleted, T? payload) =>
        new(sequence, new EntitySnapshot(type, id, 1, deleted, payload is null ? null : JsonSerializer.SerializeToElement(payload)));

    private sealed class PeerHttpHandler(IReadOnlyList<EntityChange> changes) : HttpMessageHandler
    {
        public sealed record Captured(string Method, string Path, string? Body, string? Key);
        public List<Captured> CapturedRequests { get; } = [];
        public List<long> AppliedSequences { get; } = [];
        public long FailSequence { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var key = request.Headers.TryGetValues("X-PersonalDashboard-Sync-Key", out var values) ? values.Single() : null;
            CapturedRequests.Add(new(request.Method.Method, path, body, key));
            if (path == "/api/v2/sync/changes")
            {
                var afterText = request.RequestUri.Query.TrimStart('?').Split('&').First(part => part.StartsWith("after=", StringComparison.Ordinal))[6..];
                var after = long.Parse(afterText);
                var page = new EntityChangePage(changes.Where(change => change.Sequence > after).ToArray(), changes.Max(change => change.Sequence), true);
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(page));
            }
            var seq = request.Method == HttpMethod.Post && path.EndsWith("/tasks", StringComparison.Ordinal) && body!.Contains("Task updated", StringComparison.Ordinal) ? 3
                : request.Method == HttpMethod.Post && path.EndsWith("/tasks", StringComparison.Ordinal) ? 1
                : request.Method == HttpMethod.Post && body!.Contains("edited", StringComparison.Ordinal) ? 4
                : request.Method == HttpMethod.Post ? 2
                : request.Method == HttpMethod.Delete && path.Contains("tasks", StringComparison.Ordinal) ? 5 : 6;
            AppliedSequences.Add(seq);
            if (seq == FailSequence)
            {
                FailSequence = 0;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task AncestorsAreMaterializedRootFirstAndFailedApplyDoesNotAdvanceCursorOrEchoPeerChanges()
    {
        Assert.True(V1PeerSyncPolicy.IsV1CompatibleTask("backlog", null));
        Assert.True(V1PeerSyncPolicy.IsV1CompatibleTask("today", null));
        Assert.False(V1PeerSyncPolicy.IsV1CompatibleTask("planned", null));
        Assert.False(V1PeerSyncPolicy.IsV1CompatibleTask("archived", null));
        Assert.False(V1PeerSyncPolicy.IsV1CompatibleTask("backlog", Guid.NewGuid()));
        var sectionId = Guid.NewGuid();
        Assert.Equal("Работа", V1PeerSyncPolicy.ResolveSectionName(sectionId,
            [new TaskSectionView(sectionId, "Работа", TaskLocation.Backlog, 0, 1)]));
        Assert.Throws<InvalidDataException>(() => V1PeerSyncPolicy.ResolveSectionName(sectionId, []));
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var nodes = new[]
        {
            new KnowledgeNodeView(rootId, "section", "Root", "", null, 0, 1, false, false, DateTimeOffset.UtcNow, "/Root"),
            new KnowledgeNodeView(childId, "section", "Child", "", rootId, 0, 1, false, false, DateTimeOffset.UtcNow, "/Root/Child"),
            new KnowledgeNodeView(documentId, "document", "Doc", "body", childId, 0, 1, false, false, DateTimeOffset.UtcNow, "/Root/Child/Doc")
        };
        var ancestors = V1PeerSyncPolicy.MissingAncestorsRootFirst(documentId, childId, nodes, new Dictionary<Guid, string>());
        Assert.Equal(new[] { rootId, childId }, ancestors.Select(node => node.Id));

        var changes = new[]
        {
            new EntityChange(1, new EntitySnapshot("knowledge.node", rootId, 1, false, null), ImportedFromPeer: true),
            new EntityChange(2, new EntitySnapshot("knowledge.node", childId, 1, false, null)),
            new EntityChange(3, new EntitySnapshot("knowledge.node", documentId, 1, false, null))
        };
        var applied = new List<long>();
        var advanced = new List<long>();

        await Assert.ThrowsAsync<InvalidDataException>(() => V1PeerSyncPolicy.ProcessChangesAsync(changes,
            (change, _) =>
            {
                applied.Add(change.Sequence);
                return change.Sequence == 3 ? Task.FromException(new InvalidDataException("ancestor delivery failed")) : Task.CompletedTask;
            },
            (sequence, _) => { advanced.Add(sequence); return Task.CompletedTask; }, CancellationToken.None));

        Assert.Equal(new long[] { 2, 3 }, applied);
        Assert.Equal(new long[] { 1, 2 }, advanced);
    }
}

using System.Text.Json;
using PersonalDashboard.V2.Contracts.Changes;
using PersonalDashboard.V2.Contracts.Sync;
using PersonalDashboard.V2.Tasks.Application;

namespace PersonalDashboard.V2.Tasks.Infrastructure;

public sealed class TaskGroupOrderSyncMutationHandler(TaskGroupOrderService service) : ISyncMutationHandler
{
    public string Type => "tasks.groupOrder";

    public async Task<SyncMutationResult> ApplyAsync(SyncOperation operation, CancellationToken ct = default)
    {
        var location = TaskGroupOrderIdentity.Location(operation.Id);
        if (location is null) return new(false, null, "Unknown task group order bucket.");
        var current = await service.GetAsync(location.Value, ct);
        EntitySnapshot Snapshot(TaskGroupOrderView order) => new(Type, operation.Id, order.Version, false,
            JsonSerializer.SerializeToElement(new { keys = order.Keys }));

        if (operation.Kind != SyncOperationKind.Upsert || operation.ExpectedVersion is null
            || operation.Payload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("keys", out var keysElement)
            || keysElement.ValueKind != JsonValueKind.Array)
            return new(false, Snapshot(current), "A group order upsert with keys and expected version is required.");

        string[]? keys;
        try { keys = keysElement.Deserialize<string[]>(); }
        catch (JsonException) { keys = null; }
        if (keys is null || keys.Any(key => key is null))
            return new(false, Snapshot(current), "Group order keys must be strings.");

        try
        {
            var updated = await service.ReorderAsync(location.Value, operation.ExpectedVersion.Value, keys, ct);
            return new(true, Snapshot(updated), null);
        }
        catch (TaskVersionConflictException ex) { return new(false, Snapshot(await service.GetAsync(location.Value, ct)), ex.Message); }
        catch (ArgumentException ex) { return new(false, Snapshot(await service.GetAsync(location.Value, ct)), ex.Message); }
    }
}

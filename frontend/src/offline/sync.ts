import type { OfflineEntity, OfflineStore, SyncConflict, SyncOperation, SyncPushRequest, SyncSummary, SyncTransport } from "./types";

const DEFAULT_PAGE_SIZE = 100;

/** Removes local queue metadata before sending the ASP.NET sync request. */
export function toSyncPushRequest(operations: SyncOperation[]): SyncPushRequest {
  return {
    operations: operations.map(({ operationId, type, id, expectedVersion, kind, payload }) => ({
      operationId, type, id, expectedVersion, kind, ...(payload === undefined ? {} : { payload }),
    })),
  };
}

function canRetryDelete(operation: SyncOperation, current: OfflineEntity | null): current is OfflineEntity {
  return operation.kind === "delete" && (operation.retryCount ?? 0) < 1 && current !== null && !current.deleted;
}

function deleteAlreadyApplied(operation: SyncOperation, current: OfflineEntity | null): boolean {
  return operation.kind === "delete" && (current === null || current.deleted);
}

function entityKey(type: string, id: string): string {
  return `${type}\u0000${id}`;
}

function conflictFromOperation(
  operation: SyncOperation,
  current: OfflineEntity | null,
  conflictReason: string | null,
  detectedAt: string,
): SyncConflict {
  return {
    operationId: operation.operationId,
    type: operation.type,
    id: operation.id,
    expectedVersion: operation.expectedVersion,
    localDeleted: operation.kind === "delete",
    localPayload: operation.kind === "delete" ? null : operation.payload ?? null,
    serverVersion: current?.version ?? null,
    serverPayload: current?.payload ?? null,
    serverDeleted: current?.deleted ?? null,
    conflictReason: conflictReason ?? undefined,
    detectedAt,
  };
}

/** Applies the server's ordered push results, then stores paged remote entity changes. */
export async function syncPendingOperations(
  store: OfflineStore,
  transport: SyncTransport,
  now: () => Date = () => new Date(),
  pageSize = DEFAULT_PAGE_SIZE,
): Promise<SyncSummary> {
  const epoch = await transport.getSyncEpoch();
  const localEpoch = await store.getSyncEpoch();
  if (localEpoch !== epoch) {
    // First connection must not erase edits created before the initial sync.
    if (localEpoch !== undefined) {
      if ((await store.listPendingOperations()).length) {
        throw new Error("Server data was reset. Local edits have been preserved; resolve them before switching datasets.");
      }
      await store.clearLocalData();
    }
    await store.setSyncEpoch(epoch);
  }

  const operations = await store.listPendingOperations();
  const pushResults = operations.length === 0 ? [] : await transport.pushOperations(toSyncPushRequest(operations), epoch);
  if (pushResults.length !== operations.length) {
    throw new Error("Sync transport returned a different number of push results than operations");
  }

  const summary: SyncSummary = { applied: 0, conflicts: 0, pulled: 0 };
  const detectedAt = now().toISOString();
  const deleteRetries: SyncOperation[] = [];
  for (let index = 0; index < operations.length; index++) {
    const operation = operations[index];
    const result = pushResults[index];
    if (result.operationId !== operation.operationId) {
      throw new Error("Sync transport returned push results in an unexpected order");
    }
    if (result.applied) {
      // A second edit may have been queued while this push was in flight.
      // Keep its optimistic value instead of writing the older server echo.
      const hasNewerLocalEdit = (await store.listPendingOperations()).some(pending =>
        pending.operationId !== operation.operationId
        && pending.type === operation.type
        && pending.id === operation.id,
      );
      if (result.current && !hasNewerLocalEdit) await store.putEntity(result.current);
      await store.removeOperation(operation.operationId);
      summary.applied++;
    } else if (!result.skipped) {
      if (deleteAlreadyApplied(operation, result.current)) {
        await store.resolveConflict(operation.operationId, result.current ?? undefined);
        summary.applied++;
      } else if (canRetryDelete(operation, result.current)) {
        const retry: SyncOperation = {
          ...operation,
          operationId: crypto.randomUUID(),
          expectedVersion: result.current.version,
          createdAt: detectedAt,
          retryCount: 1,
        };
        await store.replaceOperation(operation.operationId, retry);
        deleteRetries.push(retry);
      } else {
        await store.putConflict(conflictFromOperation(operation, result.current, result.conflictReason, detectedAt));
        summary.conflicts++;
      }
    }
  }

  if (deleteRetries.length > 0) {
    const retryResults = await transport.pushOperations(toSyncPushRequest(deleteRetries), epoch);
    if (retryResults.length !== deleteRetries.length)
      throw new Error("Sync transport returned a different number of delete retry results than operations");
    for (let index = 0; index < deleteRetries.length; index++) {
      const operation = deleteRetries[index];
      const result = retryResults[index];
      if (result.operationId !== operation.operationId)
        throw new Error("Sync transport returned delete retry results in an unexpected order");
      if (result.applied || deleteAlreadyApplied(operation, result.current)) {
        if (result.current) await store.putEntity(result.current);
        await store.resolveConflict(operation.operationId);
        summary.applied++;
      } else if (!result.skipped) {
        await store.putConflict(conflictFromOperation(operation, result.current, result.conflictReason, detectedAt));
        summary.conflicts++;
      }
    }
  }

  let cursor = await store.getChangeCursor();
  while (true) {
    const page = await transport.pullChanges(cursor, pageSize);
    const pendingOperations = await store.listPendingOperations();
    const pendingByEntity = new Map<string, SyncOperation>();
    for (const operation of pendingOperations) {
      const key = entityKey(operation.type, operation.id);
      if (!pendingByEntity.has(key)) pendingByEntity.set(key, operation);
    }

    for (const change of page.changes) {
      const pending = pendingByEntity.get(entityKey(change.snapshot.type, change.snapshot.id));
      if (pending) {
        if (pending.expectedVersion === null || change.snapshot.version > pending.expectedVersion) {
          await store.putConflict(conflictFromOperation(pending, change.snapshot, "The entity changed on the server while a local operation was pending.", detectedAt));
          summary.conflicts++;
        }
      } else {
        const cached = await store.getEntity(change.snapshot.type, change.snapshot.id);
        if (!cached || change.snapshot.version >= cached.version) await store.putEntity(change.snapshot);
      }
      summary.pulled++;
    }

    if (page.nextSequence < cursor || (!page.isComplete && page.nextSequence === cursor)) {
      throw new Error("Sync transport returned a non-advancing change cursor");
    }
    await store.setChangeCursor(page.nextSequence);
    cursor = page.nextSequence;
    if (page.isComplete) break;
  }

  return summary;
}

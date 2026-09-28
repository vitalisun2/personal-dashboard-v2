// Run against a production Vite preview (npm run build && npm run preview -- --port 5190).
// Uses an isolated browser profile, real IndexedDB/SW, and fixtures only: no user API writes.
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';

const base = process.env.TEST_BASE_URL || 'http://127.0.0.1:5190';
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 393, height: 873 }, serviceWorkers: 'allow' });
const page = await context.newPage();
page.setDefaultTimeout(8000);
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const project = { id: 'offline-project', title: 'Offline project', description: 'Project body', version: 1, isArchived: false, milestones: [
  { id: 'offline-epic', title: 'Offline epic', description: 'Epic body', position: 0, version: 1, features: [
    { id: 'offline-feature', title: 'Offline feature', description: 'Feature body', position: 0, version: 1, status: 'planned' },
  ] },
] };
const tasks = ['backlog', 'today', 'planned', 'archived'].map((location, position) => ({
  id: `offline-${location}`, title: `Offline ${location} task`, description: 'Task body', location, position, version: 1, workStatus: 'new',
  ...(location === 'planned' ? { projectId: project.id, milestoneId: 'offline-epic', featureId: 'offline-feature' } : { sectionId: `section-${location}` }),
}));
const sections = ['backlog', 'today'].map(location => ({ id: `section-${location}`, name: `Offline ${location} section`, location, position: 0, version: 1 }));
let mode = 'online';
let pushed = 0;
let allowApply = false;
let skippedPush = false;
const held = [];
await context.route('**/api/**', async route => {
  if (mode === 'hanging') await new Promise(resolve => held.push(resolve));
  if (mode !== 'online') return route.abort();
  const request = route.request(), url = new URL(request.url()), path = url.pathname;
  const json = value => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(value) });
  if (path === '/api/v2/sync/push') {
    const body = request.postDataJSON();
    assert.equal(body.epoch, 'offline-test-epoch');
    if (!allowApply) {
      skippedPush = true;
      return json({ results: body.operations.map(operation => ({ operationId: operation.operationId,
        applied: false, skipped: true, current: null, conflictReason: null })) });
    }
    return json({ results: body.operations.map(operation => {
      const entity = operation.type === 'planning.project' ? project : tasks.find(task => task.id === operation.id);
      assert.ok(entity, `Unexpected queued entity ${operation.type}:${operation.id}`);
      assert.equal(operation.expectedVersion, entity.version);
      assert.equal(operation.payload.operation, 'update');
      entity.title = operation.payload.title;
      entity.description = operation.payload.description;
      entity.version++;
      pushed++;
      return { operationId: operation.operationId, applied: true, skipped: false, conflictReason: null,
        current: { type: operation.type, id: entity.id, version: entity.version, deleted: false, payload: entity } };
    }) });
  }
  assert.equal(request.method(), 'GET', `Unexpected online write: ${path}`);
  if (path === '/api/v2/sync/state') return json({ epoch: 'offline-test-epoch' });
  if (path === '/api/v2/sync/changes') return json({ changes: [], nextSequence: 0, isComplete: true });
  if (path === '/api/v2/planning/projects') return json([project]);
  if (path === '/api/v2/tasks') {
    const location = url.searchParams.get('location')?.toLowerCase();
    return json(location ? tasks.filter(task => task.location === location) : tasks);
  }
  if (path === '/api/v2/tasks/sections') {
    const location = url.searchParams.get('location')?.toLowerCase();
    return json(location ? sections.filter(section => section.location === location) : sections);
  }
  if (tasks.some(task => path === `/api/v2/tasks/${task.id}`)) return json(tasks.find(task => path === `/api/v2/tasks/${task.id}`));
  if (path.includes('/groups/order')) return json({ version: 0, keys: [] });
  if (path === '/api/v2/knowledge/tree') return json([]);
  return json([]);
});

async function stored(storeName) {
  return page.evaluate(name => new Promise((resolve, reject) => {
    const open = indexedDB.open('personal-dashboard-v2');
    open.onerror = () => reject(open.error);
    open.onsuccess = () => {
      const db = open.result;
      const tx = db.transaction(name, 'readonly');
      const request = tx.objectStore(name).getAll();
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
      tx.oncomplete = () => db.close();
    };
  }), storeName);
}
async function eventually(check, message) {
  const deadline = Date.now() + 8000;
  while (Date.now() < deadline) {
    if (await check()) return;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  assert.fail(message);
}
const assertWaiting = async () => {
  await eventually(async () => (await page.locator('.sync-status').textContent()).trim() === 'Сохранено локально, ожидает синхронизации', 'Queued changes must show neutral waiting status');
  assert.equal(await page.locator('.task-error, .planning-error, .task-toast, .knowledge-toast').count(), 0);
};
const goto = path => page.goto(base + path, { waitUntil: 'domcontentloaded' });
try {
  // Opening Knowledge must prepare the other modules even if never visited online.
  await goto('/knowledge');
  await eventually(async () => {
    const entities = await stored('entities');
    return [...tasks, ...sections, project, ...project.milestones, ...project.milestones[0].features]
      .every(item => entities.some(entity => entity.id === item.id && entity.payload));
  }, 'Startup must cache all task buckets, both section lists, and the complete planning hierarchy');
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.waitForFunction(() => !!navigator.serviceWorker.controller);

  // A reachable internet connection with an unreachable Tailscale server must not block cached UI.
  mode = 'hanging';
  await goto('/tasks/offline-backlog');
  await page.locator('.task-title-open').getByText('Offline backlog task', { exact: true }).waitFor({ timeout: 2000 });
  mode = 'unavailable'; held.splice(0).forEach(resolve => resolve());
  await page.locator('.task-title-open').click();
  await page.locator('.task-title-detail-input').fill('Task edited without Tailscale');
  await page.locator('.task-title-detail-input').press('Enter');
  await eventually(async () => (await stored('operations')).some(operation => operation.id === 'offline-backlog'), 'Task edit must persist in the queue');
  await assertWaiting();

  await goto('/planning/projects/offline-project');
  await page.getByText('Offline epic', { exact: true }).waitFor();
  await page.locator('.planning-picker').click();
  await page.getByRole('button', { name: 'Редактировать Offline project', exact: true }).click();
  await page.locator('#planning-name').fill('Project edited offline');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await eventually(async () => (await stored('operations')).some(operation => operation.id === project.id), 'Project edit must persist in the queue');
  await assertWaiting();
  const pending = await stored('operations');

  // Actual network loss and a full reload exercises the cached application shell as well as data.
  await context.setOffline(true);
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.locator('.planning-picker').getByText('Project edited offline', { exact: true }).waitFor();
  await page.getByText('Offline epic', { exact: true }).waitFor();
  await goto('/planning/projects/offline-project/milestones/offline-epic/features/offline-feature');
  await page.getByText('Offline planned task', { exact: true }).waitFor();
  for (const location of ['backlog', 'today', 'archived']) {
    await goto(`/tasks/offline-${location}`);
    await page.locator('.task-title-open').getByText(location === 'backlog' ? 'Task edited without Tailscale' : `Offline ${location} task`, { exact: true }).waitFor();
  }
  assert.deepEqual((await stored('operations')).map(item => item.operationId).sort(), pending.map(item => item.operationId).sort());
  mode = 'online';
  await context.setOffline(false);
  await eventually(async () => skippedPush, 'Reconnection must attempt the queued edits');
  await page.locator('.sync-status[data-status="pending"]').waitFor({ state: 'attached' });
  await assertWaiting();
  // A successful sync round with deferred operations still receives old server snapshots.
  // Those snapshots must not roll back durable edits awaiting a later push.
  assert.deepEqual((await stored('operations')).map(item => item.operationId).sort(), pending.map(item => item.operationId).sort());
  const deferredEntities = await stored('entities');
  assert.equal(deferredEntities.find(entity => entity.type === 'tasks.task.view' && entity.id === 'offline-backlog').payload.title, 'Task edited without Tailscale');
  assert.equal(deferredEntities.find(entity => entity.type === 'planning.project.view' && entity.id === project.id).payload.title, 'Project edited offline');
  allowApply = true;
  await context.setOffline(true);
  await context.setOffline(false);
  await eventually(async () => (await stored('operations')).length === 0, 'Reconnection must drain the durable queue');
  assert.equal(pushed, pending.length);
  assert.equal(tasks[0].title, 'Task edited without Tailscale');
  assert.equal(project.title, 'Project edited offline');
  await eventually(async () => (await stored('entities')).some(entity => entity.type === 'planning.project.view' && entity.id === project.id && entity.version === project.version), 'Successful sync must refresh the cached view');
  await page.locator('.sync-status[data-status="ready"]').waitFor({ state: 'attached' });
  assert.equal((await page.locator('.sync-status').textContent()).trim(), 'Синхронизировано');
  mode = 'unavailable';
  await context.setOffline(true);
  await goto('/tasks/offline-backlog');
  await page.locator('.task-title-open').getByText('Task edited without Tailscale', { exact: true }).waitFor();
  await goto('/planning/projects/offline-project');
  await page.locator('.planning-picker').getByText('Project edited offline', { exact: true }).waitFor();
  assert.deepEqual(errors, []);
  console.log('PASS: startup prefetches all domains; stalled API does not hide cached tasks; edits survive offline PWA reload, sync on reconnect, and remain cached afterward.');
} finally {
  mode = 'unavailable'; held.splice(0).forEach(resolve => resolve());
  await browser.close();
}

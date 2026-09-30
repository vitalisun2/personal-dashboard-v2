// Run against a local Vite server. An isolated browser profile and API fixtures never touch user data.
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';

const base = process.env.TEST_BASE_URL || 'http://127.0.0.1:5190';
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 393, height: 873 }, serviceWorkers: 'block' });
const page = await context.newPage();
page.setDefaultTimeout(8000);
const errors = [];
page.on('pageerror', error => errors.push(error.message));

const features = ['feature-a', 'feature-b', 'feature-c'].map((id, position) => ({ id, title: id, description: '', position, version: 1, status: 'planned' }));
const milestones = ['epic-a', 'epic-b', 'epic-c'].map((id, position) => ({ id, title: id, description: '', position, version: 1, features: position === 0 ? features : [] }));
const project = { id: 'project', title: 'Проект', description: '', version: 1, isArchived: false, milestones };
const tasks = [
  { id: 'task-a', title: 'Задача A', position: 0, workStatus: 'new' },
  { id: 'task-b', title: 'Задача B', position: 1, workStatus: 'new' },
  { id: 'task-done', title: 'Выполнена', position: 2, workStatus: 'done' },
  { id: 'task-c', title: 'Задача C', position: 3, workStatus: 'new' },
].map(task => ({ ...task, description: '', version: 1, location: 'planned', projectId: 'project', milestoneId: 'epic-a', featureId: 'feature-a' }));

await context.route('**/api/**', route => {
  const request = route.request();
  const path = new URL(request.url()).pathname;
  const json = value => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(value) });
  if (path === '/api/v2/sync/state') return json({ epoch: 'planning-order-test' });
  if (path === '/api/v2/sync/changes') return json({ changes: [], nextSequence: 0, isComplete: true });
  if (path === '/api/v2/sync/push') return json({ results: request.postDataJSON().operations.map(operation => ({ operationId: operation.operationId, applied: false, skipped: true, current: null, conflictReason: null })) });
  if (path === '/api/v2/planning/projects') return json([project]);
  if (path === '/api/v2/tasks') return json(tasks);
  if (path === '/api/v2/tasks/sections') return json([]);
  if (path.includes('/groups/order')) return json({ version: 0, keys: [] });
  return json([]);
});

const rows = () => page.locator('.planning-order-row').evaluateAll(items => items.map(item => item.dataset.orderId));
const stored = storeName => page.evaluate(name => new Promise((resolve, reject) => {
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

try {
  for (const scenario of [
    { path: '/planning/projects/project', source: 'epic-b', target: 'epic-a', before: ['epic-a', 'epic-b', 'epic-c'], after: ['epic-b', 'epic-a', 'epic-c'] },
    { path: '/planning/projects/project/milestones/epic-a', source: 'feature-b', target: 'feature-a', before: ['feature-a', 'feature-b', 'feature-c'], after: ['feature-b', 'feature-a', 'feature-c'] },
    { path: '/planning/projects/project/milestones/epic-a/features/feature-a', source: 'task-b', target: 'task-a', before: ['task-a', 'task-b', 'task-done', 'task-c'], after: ['task-b', 'task-a', 'task-done', 'task-c'] },
  ]) {
    await page.goto(base + scenario.path);
    await page.getByRole('button', { name: 'Включить сортировку' }).click();
    assert.deepEqual(await rows(), scenario.before);

    const source = await page.locator(`[data-order-id="${scenario.source}"] .planning-order-handle`).boundingBox();
    const target = await page.locator(`[data-order-id="${scenario.target}"]`).boundingBox();
    assert.ok(source && target);
    await page.mouse.move(source.x + source.width / 2, source.y + source.height / 2);
    await page.mouse.down();
    await page.mouse.move(source.x + source.width / 2, target.y + 2, { steps: 8 });
    assert.equal(await page.locator('.reorder-ghost').count(), 1);
    await page.mouse.up();
    assert.equal(await page.locator('.reorder-ghost').count(), 0);
    await page.waitForFunction(id => document.querySelector('.planning-order-row')?.dataset.orderId === id, scenario.source);
    assert.deepEqual(await rows(), scenario.after);
    assert.equal(await page.locator('.planning-order-toggle').getAttribute('aria-pressed'), 'true');

    const operations = await stored('operations');
    const operation = operations.find(item => item.id === scenario.source);
    assert.ok(operation, `Dragged ${scenario.source} must own the queued reorder`);
    assert.deepEqual(operation.payload.order.map(item => item.id), scenario.after);

    await page.reload();
    await page.getByRole('button', { name: 'Включить сортировку' }).click();
    assert.deepEqual(await rows(), scenario.after);
  }
  const cached = (await stored('entities')).filter(item => item.type === 'tasks.task.view');
  assert.deepEqual(['task-b', 'task-a', 'task-done', 'task-c'].map(id => cached.find(item => item.id === id).payload.planningPosition), [0, 1, 2, 3]);
  assert.deepEqual(tasks.map(task => cached.find(item => item.id === task.id).payload.position), tasks.map(task => task.position), 'Planning display reorder must preserve task bucket positions');
  assert.deepEqual(errors, []);
  console.log('PASS: epic, feature and task reorder changes on drop, queues the dragged item and full order, and survives reload.');
} finally {
  await browser.close();
}

// Run against a local Vite server. API fixtures never touch user data.
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';

const base = process.env.TEST_BASE_URL || 'http://127.0.0.1:5190';
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 393, height: 873 }, serviceWorkers: 'block' });
const page = await context.newPage();
page.setDefaultTimeout(5000);
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const features = Array.from({ length: 24 }, (_, i) => ({ id: `feature-${i}`, title: `Фича ${i}`, description: '', position: i, version: 1, status: 'planned' }));
const milestones = Array.from({ length: 24 }, (_, i) => ({ id: `epic-${i}`, title: `Эпик ${i}`, description: '', position: i, version: 1, features: i === 0 ? features : [] }));
const project = { id: 'project', title: 'Проект', description: '', version: 1, isArchived: false, milestones };
let tasks = Array.from({ length: 24 }, (_, i) => ({ id: `task-${i}`, title: `Задача ${i}`, description: '', position: i, version: 1, projectId: 'project', milestoneId: 'epic-0', featureId: 'feature-0', location: 'planned', workStatus: 'new' }));
let releaseRefresh;
let refreshGate;
let refreshStarted = false;
let writes = 0;
let rejectWrite = false;
const reorder = (items, ids) => ids.map((id, position) => ({ ...items.find(item => item.id === id), position, version: items.find(item => item.id === id).version + 1 }));
await context.route('**/api/**', async route => {
  const request = route.request(), path = new URL(request.url()).pathname;
  const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
  if (path.includes('/sync/')) return route.abort();
  if (request.method() === 'PUT') {
    if (rejectWrite) return json({ error: 'Порядок уже изменён. Обновите страницу.' }, 409);
    const body = request.postDataJSON();
    if (path.endsWith('/milestones/order')) {
      assert.equal(body.expectedVersion, project.version);
      project.milestones = reorder(project.milestones, body.ids);
      project.version++;
    } else if (path.endsWith('/features/order')) {
      const epic = project.milestones.find(item => item.id === 'epic-0');
      assert.equal(body.expectedVersion, epic.version);
      epic.features = reorder(epic.features, body.ids);
      epic.version++; project.version++;
    } else if (path === '/api/v2/tasks/order') {
      for (const item of body.items) assert.equal(item.expectedVersion, tasks.find(task => task.id === item.id).version);
      tasks = reorder(tasks, body.items.map(item => item.id));
    } else throw new Error(`Unexpected write: ${path}`);
    writes++;
    refreshStarted = false;
    refreshGate = new Promise(resolve => { releaseRefresh = resolve; });
    return json(path === '/api/v2/tasks/order' ? tasks : project);
  }
  if (path === '/api/v2/planning/projects') {
    if (refreshGate) { refreshStarted = true; await refreshGate; }
    return json([project]);
  }
  if (path === '/api/v2/tasks') return json(tasks);
  return json([]);
});

const rowIds = () => page.locator('.planning-order-row').evaluateAll(rows => rows.map(row => row.dataset.orderId));
async function drag(sourceId, targetId) {
  const handle = page.locator(`[data-order-id="${sourceId}"] .planning-order-handle`);
  const source = await handle.boundingBox();
  const target = await page.locator(`[data-order-id="${targetId}"]`).boundingBox();
  assert.ok(source && target);
  await page.mouse.move(source.x + source.width / 2, source.y + source.height / 2);
  await page.mouse.down();
  await page.mouse.move(source.x + source.width / 2, target.y + 2, { steps: 8 });
  assert.equal(await page.locator('.reorder-ghost').count(), 1);
  await page.mouse.up();
  assert.equal(await page.locator('.reorder-ghost').count(), 0);
}

try {
  const root = '/planning/projects/project';
  for (const path of [root, `${root}/milestones/epic-0`, `${root}/milestones/epic-0/features/feature-0`]) {
    await page.goto(base + path);
    await page.getByRole('button', { name: 'Включить сортировку', exact: true }).click();
    const before = await rowIds();
    assert.equal(before.length, 24);
    await page.locator('.planning-scroll').evaluate(element => { element.scrollTop = 160; });
    // Preserve DOM identity and scroll position throughout the delayed server refresh.
    const scroll = await page.locator('.planning-scroll').elementHandle();
    const row = await page.locator(`[data-order-id="${before[5]}"]`).elementHandle();
    const scrollTop = await scroll.evaluate(element => element.scrollTop);
    assert.ok(scrollTop > 0);
    const oldWrites = writes;
    await drag(before[5], before[4]);
    await assert.doesNotReject(async () => {
      const deadline = Date.now() + 5000;
      while (!refreshStarted && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 20));
      assert.ok(refreshStarted, 'reorder must reach the delayed refresh');
    });
    assert.equal(writes, oldWrites + 1);
    assert.equal(await page.getByText('Загружаем план…', { exact: true }).count(), 0);
    assert.ok(await scroll.evaluate(element => element.isConnected));
    assert.ok(await row.evaluate(element => element.isConnected));
    assert.equal(await scroll.evaluate(element => element.scrollTop), scrollTop);
    releaseRefresh(); refreshGate = null;
    const expected = [...before];
    expected.splice(4, 0, expected.splice(5, 1)[0]);
    await page.waitForFunction(ids => JSON.stringify([...document.querySelectorAll('.planning-order-row')].map(row => row.dataset.orderId)) === JSON.stringify(ids), expected);
    assert.ok(await scroll.evaluate(element => element.isConnected));
    assert.ok(await row.evaluate(element => element.isConnected));
    assert.equal(await scroll.evaluate(element => element.scrollTop), scrollTop);
    assert.equal(await page.getByRole('button', { name: 'Готово', exact: true }).getAttribute('aria-pressed'), 'true');
    // A rejected save preserves the list and reports the conflict.
    rejectWrite = true;
    await drag(expected[5], expected[4]);
    await page.getByRole('alert').waitFor();
    assert.deepEqual(await rowIds(), expected);
    assert.ok(await scroll.evaluate(element => element.isConnected));
    rejectWrite = false;
    await page.reload();
    await page.getByRole('button', { name: 'Включить сортировку', exact: true }).click();
    assert.deepEqual(await rowIds(), expected);
  }
  assert.deepEqual(errors, []);
  console.log('PASS: epic, feature and task drag preserves DOM, scroll and order mode during refresh; saved order survives reload; conflicts preserve rows.');
} finally {
  releaseRefresh?.();
  await browser.close();
}

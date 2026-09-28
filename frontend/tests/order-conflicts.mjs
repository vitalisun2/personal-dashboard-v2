// Isolated browser/IndexedDB fixtures only. Run against a freshly built Vite preview.
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 393, height: 873 }, serviceWorkers: 'block' });
const page = await context.newPage();
page.setDefaultTimeout(10000);
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const groupId = '3be630ad-c973-4cb2-b8d3-6374d87c8355';
const scopes = [
  { name: 'tasks-section', type: 'tasks.task', bucket: 'backlog', sectionId: 'section' },
  { name: 'tasks-project', type: 'tasks.task', bucket: 'today', planning: { projectId: 'project' } },
  { name: 'tasks-planned', type: 'tasks.task', placement: 'planned', planning: { projectId: 'project', milestoneId: 'epic', featureId: 'feature' } },
  { name: 'projects', type: 'planning.project' },
  { name: 'epics', type: 'planning.milestone', projectId: 'project', expectedParentVersion: 2 },
  { name: 'features', type: 'planning.feature', projectId: 'project', milestoneId: 'epic', expectedParentVersion: 2 },
  { name: 'sections', type: 'tasks.section', bucket: 'backlog' },
  { name: 'groups', type: 'tasks.groupOrder', bucket: 'backlog' },
  { name: 'knowledge', type: 'knowledge.node', parentId: null },
];
let fixture;
let unavailable = false;
await context.route('**/api/**', route => {
  const path = new URL(route.request().url()).pathname;
  if (unavailable || route.request().method() !== 'GET' || path.includes('/sync/') || !fixture) return route.abort();
  let body = [];
  if (path.endsWith('/planning/projects')) body = fixture.projects;
  else if (path.endsWith('/tasks/sections')) body = fixture.sections;
  else if (path.endsWith('/tasks/groups/order')) body = fixture.groups;
  else if (path.endsWith('/tasks')) body = fixture.tasks;
  else if (path.endsWith('/knowledge/tree')) body = fixture.knowledge;
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
});

async function stored() {
  return page.evaluate(() => new Promise((resolve, reject) => {
    const request = indexedDB.open('personal-dashboard-v2');
    request.onerror = () => reject(request.error);
    request.onsuccess = () => {
      const db = request.result, result = {}, tx = db.transaction(['entities', 'operations', 'conflicts'], 'readonly');
      for (const name of ['entities', 'operations', 'conflicts']) {
        const read = tx.objectStore(name).getAll(); read.onsuccess = () => { result[name] = name === 'entities'
          ? read.result.filter(entity => entity.type !== 'sync.orderSnapshot') : read.result; };
      }
      tx.oncomplete = () => { db.close(); resolve(result); }; tx.onerror = () => reject(tx.error);
    };
  }));
}

async function seed(scope) {
  const isGroup = scope.type === 'tasks.groupOrder';
  const ids = isGroup ? ['section:a', 'section:b', 'section:c', 'section:d'] : ['a', 'b', 'c', 'd'];
  const [a, b, c, d] = ids;
  const row = (id, position, version) => ({ id, title: `Title ${id}`, name: `Title ${id}`, description: 'Content survives order resolution',
    version, position, location: scope.placement ?? scope.bucket ?? 'backlog', sectionId: scope.sectionId ?? null,
    ...scope.planning, projectId: scope.projectId ?? scope.planning?.projectId, milestoneId: scope.milestoneId ?? scope.planning?.milestoneId,
    features: [], milestones: [], isArchived: false, status: 'planned', parentId: null, kind: 'document', markdown: 'Document body' });
  const server = [row(a, 0, 7), row(b, 1, 8), row(c, 2, 9)];
  const local = [row(b, 0, 3), row(a, 1, 3), row(d, 2, 3)];
  const project = { id: 'project', title: 'Project', version: 20, position: 0, isArchived: false, milestones: [
    { id: 'epic', title: 'Epic', version: 30, position: 0, features: [{ id: 'feature', title: 'Feature', version: 40, position: 0 }] },
  ] };
  fixture = { tasks: scope.type === 'tasks.task' ? server : [],
    sections: scope.type === 'tasks.section' ? server : [{ id: 'section', name: 'Section', location: 'backlog', version: 1 }],
    projects: scope.type === 'planning.project' ? server : [structuredClone(project)],
    groups: { version: 7, keys: [a, b, c] }, knowledge: scope.type === 'knowledge.node' ? server : [] };
  if (isGroup) fixture.sections = server.map(item => ({ ...item, id: item.id.slice('section:'.length) }));
  if (scope.type === 'planning.milestone') fixture.projects[0].milestones = server;
  if (scope.type === 'planning.feature') fixture.projects[0].milestones[0].features = server;
  const entities = local.flatMap(payload => [{ type: scope.type, id: payload.id, version: payload.version, deleted: false, payload },
    { type: `${scope.type}.view`, id: payload.id, version: payload.version, deleted: false, payload }]);
  if (scope.type === 'planning.milestone' || scope.type === 'planning.feature') {
    if (scope.type === 'planning.milestone') project.milestones = local;
    else project.milestones[0].features = local;
    entities.push({ type: 'planning.project.view', id: 'project', version: 2, deleted: false, payload: project });
  }
  if (isGroup) entities.push({ type: scope.type, id: groupId, version: 3, deleted: false, payload: { keys: [b, a, d] } });
  if (isGroup) entities.push(...local.map(item => ({ type: 'tasks.section.view', id: item.id.slice('section:'.length), version: 3,
    deleted: false, payload: { ...item, id: item.id.slice('section:'.length) } })));
  const operations = [a, b].map((id, index) => ({ operationId: `order-${index}`, type: scope.type, id: isGroup ? groupId : id,
    expectedVersion: 2, kind: 'upsert', createdAt: `2026-09-28T10:00:0${index}.000Z`,
    payload: isGroup ? { keys: [b, a, d] } : { ...scope, id, operation: 'reorder', kind: scope.type.split('.')[1], expectedVersion: 2,
      order: [b, a, d].map(id => ({ id, expectedVersion: 2 })) } }));
  // Same source entity also has a content edit: choosing an order must preserve it.
  if (!isGroup) operations.push({ operationId: 'preserve-content', type: scope.type, id: a, expectedVersion: 3, kind: 'upsert',
    payload: { operation: 'update', title: 'Keep my edited title' }, createdAt: '2026-09-28T10:00:02.000Z' });
  operations.push({ operationId: 'preserve-unrelated', type: 'knowledge.node', id: 'unrelated', expectedVersion: 1,
    kind: 'upsert', payload: { title: 'Unrelated edit' }, createdAt: '2026-09-28T10:00:03.000Z' });
  const conflicts = operations.slice(0, 2).map((op, index) => ({ operationId: op.operationId, type: op.type, id: op.id,
    expectedVersion: 2, localDeleted: false, localPayload: op.payload, serverVersion: isGroup ? 7 : server[index].version,
    serverPayload: isGroup ? fixture.groups : server[index], serverDeleted: false, detectedAt: '2026-09-28T10:00:04.000Z' }));
  await page.evaluate(data => new Promise((resolve, reject) => {
    const request = indexedDB.open('personal-dashboard-v2');
    request.onsuccess = () => {
      const db = request.result, tx = db.transaction(['entities', 'operations', 'conflicts'], 'readwrite');
      for (const [name, records] of Object.entries(data)) {
        tx.objectStore(name).clear(); records.forEach(record => tx.objectStore(name).put(record));
      }
      tx.oncomplete = () => { db.close(); window.dispatchEvent(new Event('offline-data-updated')); resolve(); };
      tx.onerror = () => reject(tx.error);
    }; request.onerror = () => reject(request.error);
  }), { entities, operations, conflicts });
  return { a, b, c, d, isGroup };
}

try {
  await page.goto((process.env.TEST_BASE_URL || 'http://127.0.0.1:5190') + '/knowledge', { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(async () => (await indexedDB.databases()).some(db => db.name === 'personal-dashboard-v2'));
  for (const scope of scopes) {
    for (const choice of ['server', 'local']) {
      const { a, b, c, d, isGroup } = await seed(scope);
      const before = await stored();
      await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
      await page.getByRole('tabpanel').waitFor();
      assert.deepEqual(await page.locator('.conflict-order li').allTextContents(), [`Title ${b}`, `Title ${a}`, `Title ${c}`], `${scope.name}: reconciled local order preview`);
      await page.locator('.conflict-order-notes').getByText(`Новые элементы с сервера добавлены в конец порядка устройства: Title ${c}.`, { exact: true }).waitFor();
      await page.locator('.conflict-order-notes').getByText(`Удалённые или перенесённые на сервере элементы исключены из порядка устройства: Title ${d}.`, { exact: true }).waitFor();
      if (scope.name === 'tasks-section' && choice === 'local' && process.env.TEST_SCREENSHOT_PATH)
        await page.screenshot({ path: process.env.TEST_SCREENSHOT_PATH, fullPage: true });
      await page.getByRole('tab', { name: 'На сервере', exact: true }).click();
      assert.deepEqual(await page.locator('.conflict-order li').allTextContents(), [`Title ${a}`, `Title ${b}`, `Title ${c}`], `${scope.name}: remote order preview`);
      await page.getByRole('button', { name: 'Решить позже', exact: true }).click();
      assert.deepEqual(await stored(), before, `${scope.name}: cancel changes no stored data`);
      await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
      await page.getByRole('tab', { name: choice === 'local' ? 'На этом устройстве' : 'На сервере', exact: true }).click();
      await page.getByRole('button', { name: choice === 'local' ? 'Выбрать порядок устройства' : 'Выбрать порядок сервера', exact: true }).click();
      await page.getByRole('dialog').waitFor({ state: 'hidden' });
      const after = await stored();
      assert.equal(after.conflicts.length, 0, `${scope.name}: both reorder conflicts cleared`);
      for (const preserved of before.operations.filter(op => op.operationId.startsWith('preserve-')))
        assert.ok(after.operations.some(op => op.operationId === preserved.operationId && JSON.stringify(op.payload) === JSON.stringify(preserved.payload)), `${scope.name}: ${preserved.operationId} survives`);
      const reordered = after.operations.filter(op => !op.operationId.startsWith('preserve-'));
      assert.equal(reordered.length, choice === 'local' ? 1 : 0, scope.name);
      if (choice === 'local') {
        const operation = reordered[0];
        if (isGroup) { assert.equal(operation.expectedVersion, 7); assert.deepEqual(operation.payload.keys, [b, a, c]); }
        else {
          assert.equal(operation.payload.operation, 'reorder', scope.name);
          assert.deepEqual(operation.payload.order.map(({ id, expectedVersion }) => ({ id, expectedVersion })),
            [{ id: b, expectedVersion: 8 }, { id: a, expectedVersion: 7 }, { id: c, expectedVersion: 9 }], scope.name);
          if (scope.type === 'knowledge.node') assert.deepEqual(operation.payload.order.map(item => item.previousPosition), [1, 0, 2]);
          if (scope.type === 'planning.milestone') assert.equal(operation.payload.expectedParentVersion, 20);
          if (scope.type === 'planning.feature') assert.equal(operation.payload.expectedParentVersion, 30);
        }
      }
    }
  }
  await seed(scopes[0]);
  await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
  const before = await stored();
  fixture.tasks[0].version++;
  await page.getByRole('button', { name: 'Выбрать порядок устройства', exact: true }).click();
  await page.getByRole('alert').waitFor();
  assert.deepEqual(await stored(), before, 'A changed remote collection rejects a stale decision without discarding anything');
  await page.getByRole('button', { name: 'Решить позже', exact: true }).click();
  await seed(scopes[0]);
  await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
  const offlineBefore = await stored();
  unavailable = true;
  await page.getByRole('tab', { name: 'На сервере', exact: true }).click();
  await page.getByRole('button', { name: 'Выбрать порядок сервера', exact: true }).click();
  await page.getByRole('alert').waitFor();
  assert.deepEqual(await stored(), offlineBefore, 'Connection loss during confirmation cannot silently apply a cached order');
  assert.deepEqual(errors, []);
  console.log('PASS: 9 order scopes × local/server, grouped conflicts, queue isolation, full versioned reorder, parent versions, additions/deletions, cancel, stale server, offline confirmation');
} finally { await browser.close(); }

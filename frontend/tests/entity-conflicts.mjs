// Run against Vite preview after npm run build. All data lives in an isolated
// browser profile; every API request is intercepted and no user data is touched.
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';

const base = process.env.TEST_BASE_URL || 'http://127.0.0.1:5190';
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 393, height: 873 }, serviceWorkers: 'block' });
const page = await context.newPage();
page.setDefaultTimeout(8000);
const errors = [];
page.on('pageerror', error => errors.push(error.message));
await context.route('**/api/**', route => route.abort());

async function snapshot() {
  return page.evaluate(() => new Promise((resolve, reject) => {
    const request = indexedDB.open('personal-dashboard-v2');
    request.onerror = () => reject(request.error);
    request.onsuccess = async () => {
      const db = request.result;
      const tx = db.transaction(['entities', 'operations', 'conflicts'], 'readonly');
      const read = name => new Promise((done, fail) => {
        const item = tx.objectStore(name).getAll();
        item.onsuccess = () => done(item.result); item.onerror = () => fail(item.error);
      });
      const [entities, operations, conflicts] = await Promise.all(['entities', 'operations', 'conflicts'].map(read));
      db.close(); resolve({ entities, operations, conflicts });
    };
  }));
}

async function seed(type, options = {}) {
  return page.evaluate(async ({ type, options }) => {
    const id = 'conflicted', time = '2026-09-28T10:00:00.000Z';
    const local = { id, title: `Local ${type}`, description: 'Complete local description', version: 3,
      location: 'planned', workStatus: 'done', position: 9, projectId: 'parent-project', milestoneId: 'parent-epic',
      featureId: 'parent-feature', status: 'active', isArchived: true, kind: 'document', markdown: '# Local document', parentId: null, ...options.local };
    const server = { ...local, title: `Server ${type}`, description: 'Server description', markdown: '# Server document', version: 7, ...options.server };
    const entity = { type, id, payload: local, version: 3, deleted: false };
    const entities = [entity, ...(options.entities ?? [])];
    if (type !== 'knowledge.node') entities.push({ ...entity, type: `${type}.view` });
    if (type === 'planning.milestone' || type === 'planning.feature') {
      const milestone = type === 'planning.milestone' ? { ...local, features: [] } : { id: 'parent-epic', title: 'Parent epic', features: [local] };
      entities.push({ type: 'planning.project.view', id: 'parent-project', version: 1, deleted: false,
        payload: { id: 'parent-project', title: 'Unchanged parent', milestones: [milestone] } });
    }
    const operations = [0, 1].map(index => ({ type, id, operationId: `conflict-${index}`, expectedVersion: index + 1,
      kind: options.localDeleted ? 'delete' : 'upsert', payload: { operation: 'update', title: index ? local.title : 'Earlier edit', projectId: 'parent-project', milestoneId: 'parent-epic' },
      createdAt: `2026-09-28T10:00:0${index}.000Z` }));
    operations.push({ type: 'tasks.task', id: 'unrelated', operationId: 'keep-this-operation', expectedVersion: 1,
      kind: 'upsert', payload: { operation: 'update', title: 'Unrelated edit' }, createdAt: time });
    const conflicts = operations.slice(0, 2).map(operation => ({ operationId: operation.operationId, type, id,
      expectedVersion: operation.expectedVersion, localDeleted: !!options.localDeleted, localPayload: operation.payload,
      serverPayload: options.serverDeleted ? null : server, serverVersion: 7, serverDeleted: !!options.serverDeleted, detectedAt: time }));
    await new Promise((resolve, reject) => {
      const request = indexedDB.open('personal-dashboard-v2');
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result, tx = db.transaction(['entities', 'operations', 'conflicts'], 'readwrite');
        for (const [name, records] of Object.entries({ entities, operations, conflicts })) {
          const store = tx.objectStore(name); store.clear(); records.forEach(record => store.put(record));
        }
        tx.oncomplete = () => { db.close(); resolve(); };
        tx.onerror = () => reject(tx.error);
      };
    });
    window.dispatchEvent(new Event('offline-data-updated'));
    return { local, server };
  }, { type, options });
}

try {
  await page.goto(base + '/knowledge', { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(async () => (await indexedDB.databases()).some(db => db.name === 'personal-dashboard-v2'));
  for (const type of ['tasks.task', 'knowledge.node', 'planning.project', 'planning.milestone', 'planning.feature']) {
    for (const choice of ['server', 'local']) {
      const fixture = await seed(type);
      const before = await snapshot();
      await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
      const localCard = page.getByRole('region', { name: 'На этом устройстве', exact: true });
      const serverCard = page.getByRole('region', { name: 'На сервере', exact: true });
      await localCard.getByText(fixture.local.title, { exact: true }).waitFor();
      await serverCard.getByText(fixture.server.title, { exact: true }).waitFor();
      assert.deepEqual(await localCard.locator('dt').allTextContents(), await serverCard.locator('dt').allTextContents());
      assert.ok(!(await localCard.locator('dt').allTextContents()).includes('Статус'), 'Matching status stays hidden');
      const text = type === 'knowledge.node' ? 'markdown' : 'description';
      assert.equal(await localCard.getByText(fixture.local[text], { exact: true }).isVisible(), false, 'Long text starts collapsed');
      await localCard.locator('summary').click();
      await serverCard.locator('summary').click();
      await localCard.getByText(fixture.local[text], { exact: true }).waitFor();
      await serverCard.getByText(fixture.server[text], { exact: true }).waitFor();
      if (type === 'planning.feature' && choice === 'local' && process.env.TEST_SCREENSHOT_PATH)
        await page.screenshot({ path: process.env.TEST_SCREENSHOT_PATH, fullPage: true });
      await page.getByRole('button', { name: 'Решить позже', exact: true }).click();
      assert.deepEqual(await snapshot(), before, `${type}: cancelling must preserve both versions and queue`);
      await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
      await page.getByRole('button', { name: choice === 'server' ? 'Оставить вариант сервера' : 'Оставить вариант устройства', exact: true }).click();
      await page.getByRole('dialog').waitFor({ state: 'hidden' });
      const after = await snapshot();
      assert.equal(after.conflicts.length, 0, `${type}: clears every old conflict of the entity`);
      assert.deepEqual(after.operations.find(item => item.id === 'unrelated'), before.operations.find(item => item.id === 'unrelated'));
      const queue = after.operations.filter(item => item.id === 'conflicted');
      assert.equal(queue.length, choice === 'server' ? 0 : 1);
      const entity = after.entities.find(item => item.type === type && item.id === 'conflicted');
      assert.equal(entity.payload.title, fixture[choice].title);
      assert.equal(entity.payload.description, fixture[choice].description);
      if (choice === 'local') {
        const replacement = queue[0];
        assert.equal(replacement.expectedVersion, 7);
        assert.equal(replacement.payload.title, fixture.local.title);
        assert.equal(replacement.payload.description, fixture.local.description);
        assert.ok(!['conflict-0', 'conflict-1'].includes(replacement.operationId));
        if (type === 'knowledge.node') assert.equal(replacement.payload.markdown, fixture.local.markdown);
        else assert.equal(replacement.payload.operation, 'replace');
        if (type === 'tasks.task') {
          assert.equal(replacement.payload.placement, 'planned');
          assert.equal(replacement.payload.workStatus, 'done');
          assert.equal(replacement.payload.planning.featureId, 'parent-feature');
        }
      }
      if (type === 'planning.milestone' || type === 'planning.feature') {
        const project = after.entities.find(item => item.type === 'planning.project.view').payload;
        const nested = type === 'planning.milestone' ? project.milestones[0] : project.milestones[0].features[0];
        assert.equal(nested.title, fixture[choice].title, 'Nested planning view follows the chosen version');
        assert.equal(project.title, 'Unchanged parent');
      }
    }
  }
  // One compact pair of cards for status/location differences; real responsive layout.
  await seed('tasks.task', {
    local: { title: 'Проверить оплату', description: 'Проверить оплату на телефоне', location: 'today', workStatus: 'inProgress',
      projectId: 'site', milestoneId: 'launch', featureId: null },
    server: { title: 'Проверить оплату', description: 'Проверить оплату и возврат', location: 'today', workStatus: 'done',
      projectId: 'site', milestoneId: 'improvements', featureId: null },
    entities: [{ type: 'planning.project.view', id: 'site', version: 1, deleted: false, payload: { title: 'Сайт', milestones: [
      { id: 'launch', title: 'Запуск', features: [] }, { id: 'improvements', title: 'Доработки', features: [] },
    ] } }],
  });
  await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
  await page.getByRole('region', { name: 'На этом устройстве', exact: true }).waitFor();
  assert.deepEqual(await page.locator('.conflict-card').first().locator('dt').allTextContents(), ['Описание', 'Статус', 'Расположение']);
  await page.getByText('Сегодня → Сайт → Запуск', { exact: true }).waitFor();
  await page.getByText('Сегодня → Сайт → Доработки', { exact: true }).waitFor();
  for (const [name, viewport] of [['mobile', { width: 393, height: 873 }], ['desktop', { width: 1280, height: 900 }]]) {
    await page.setViewportSize(viewport);
    const boxes = await page.locator('.conflict-card').evaluateAll(cards => cards.map(card => {
      const rect = card.getBoundingClientRect(); return { x: rect.x, y: rect.y, right: rect.right };
    }));
    assert.ok(boxes.every(box => box.x >= 0 && box.right <= viewport.width), 'Cards fit viewport');
    if (name === 'mobile') assert.ok(boxes[1].y > boxes[0].y, 'Mobile cards stack');
    else assert.equal(boxes[0].y, boxes[1].y, 'Desktop cards are side by side');
    if (process.env.TEST_SCREENSHOT_DIR) {
      await mkdir(process.env.TEST_SCREENSHOT_DIR, { recursive: true });
      await page.screenshot({ path: join(process.env.TEST_SCREENSHOT_DIR, `${name}.png`), fullPage: true });
    }
  }
  await page.getByRole('button', { name: 'Решить позже', exact: true }).click();
  await page.setViewportSize({ width: 393, height: 873 });
  // Server deletion leaves local text available, but cannot be silently resurrected.
  await seed('knowledge.node', { serverDeleted: true });
  await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
  assert.equal(await page.getByRole('button', { name: 'Оставить вариант устройства', exact: true }).isDisabled(), true);
  await page.getByRole('region', { name: 'На этом устройстве', exact: true }).locator('summary').click();
  await page.getByText('# Local document', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Подтвердить удаление', exact: true }).click();
  await page.getByRole('dialog').waitFor({ state: 'hidden' });
  assert.equal((await snapshot()).entities.find(entity => entity.type === 'knowledge.node' && entity.id === 'conflicted').deleted, true);
  // Local deletion is labelled explicitly and queues only the chosen delete.
  await seed('knowledge.node', { localDeleted: true });
  await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
  await page.getByRole('region', { name: 'На этом устройстве', exact: true }).getByText('Удалена', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Подтвердить удаление', exact: true }).click();
  await page.getByRole('dialog').waitFor({ state: 'hidden' });
  assert.equal((await snapshot()).operations.find(operation => operation.id === 'conflicted').kind, 'delete');
  // Another local edit while the preview is open must invalidate that decision.
  await seed('tasks.task');
  await page.getByRole('button', { name: 'Разобрать конфликты · 1', exact: true }).click();
  await page.evaluate(() => new Promise((resolve, reject) => {
    const request = indexedDB.open('personal-dashboard-v2');
    request.onsuccess = () => {
      const db = request.result, tx = db.transaction('operations', 'readwrite');
      tx.objectStore('operations').add({ type: 'tasks.task', id: 'conflicted', operationId: 'concurrent-edit',
        expectedVersion: 3, kind: 'upsert', payload: { operation: 'update', title: 'Newest local edit' }, createdAt: '2026-09-28T10:00:03.000Z' });
      tx.oncomplete = () => { db.close(); resolve(); }; tx.onerror = () => reject(tx.error);
    };
    request.onerror = () => reject(request.error);
  }));
  const concurrent = await snapshot();
  await page.getByRole('button', { name: 'Оставить вариант устройства', exact: true }).click();
  await page.getByRole('alert').filter({ hasText: 'Версии изменились' }).waitFor();
  assert.deepEqual(await snapshot(), concurrent, 'A stale preview cannot discard concurrent local edits');
  assert.deepEqual(errors, []);
  console.log('PASS: five entity types, difference-only cards, collapsed text, mobile/desktop layout, deletions, cancel, local/server resolution, queue isolation, nested planning views, stale-preview protection');
} finally { await browser.close(); }

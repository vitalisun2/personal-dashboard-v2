// Run against a local Vite server. All API requests are intercepted; no user data is changed.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright-core';

const base = process.env.TEST_BASE_URL || 'http://127.0.0.1:5190';
const output = process.env.TEST_OUTPUT_DIR || 'test-results/planning-progress';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 393, height: 873 }, serviceWorkers: 'block' });
const page = await context.newPage();
page.setDefaultTimeout(5000);
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const feature = { id: 'feature', title: 'Доработка фонов', description: '', version: 1, position: 0, status: 'planned' };
const milestone = { id: 'epic', title: 'Арт', version: 1, position: 0, description: '', features: [feature, { ...feature, id: 'other', title: 'Декор' }] };
const project = { id: 'project', title: 'Lch', version: 1, isArchived: false, description: '', milestones: [milestone, { ...milestone, id: 'empty', title: 'Звуки', features: [] }] };
let tasks = Array.from({ length: 8 }, (_, i) => ({ id: `task-${i}`, title: `Фон ${i + 1}`, description: '', projectId: 'project', milestoneId: 'epic', featureId: 'feature', location: i < 2 ? 'archived' : 'planned', workStatus: i < 2 ? 'done' : 'new', position: i, version: 1 }));
let rejectDelete = false;
let rejectStatus = false;
let offlineWrites = false;
const writes = [];
await context.route('**/api/**', async route => {
  const request = route.request(), url = new URL(request.url()), path = url.pathname;
  const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
  if (path.includes('/sync/')) return route.abort();
  if (request.method() !== 'GET') {
    if (offlineWrites) return route.abort();
    writes.push({ path, method: request.method(), body: request.postDataJSON() });
    if (path.endsWith('/features/feature/status')) {
      if (rejectStatus) return json({ error: 'Фича уже изменена. Обновите страницу.' }, 409);
      assert.equal(request.postDataJSON().expectedVersion, feature.version);
      feature.status = request.postDataJSON().status; feature.version++; milestone.version++; project.version++;
      return json(project);
    }
    if (request.method() === 'DELETE') {
      if (rejectDelete) return json({ error: 'Задача уже изменена. Обновите страницу.' }, 409);
      const id = path.split('/').at(-1);
      tasks = tasks.filter(task => task.id !== id);
      return route.fulfill({ status: 204 });
    }
    throw new Error(`Unexpected mutation: ${request.method()} ${path}`);
  }
  if (path === '/api/v2/planning/projects') return json([project]);
  if (path === '/api/v2/tasks') {
    const location = url.searchParams.get('location')?.toLowerCase();
    return json(tasks.filter(task => !location || task.location === location));
  }
  if (path.endsWith('/sections')) return json([]);
  if (path.endsWith('/groups/order')) return json({ version: 0, keys: [] });
  if (path.startsWith('/api/v2/tasks/')) return json(tasks.find(task => task.id === path.split('/').at(-1)));
  return json([]);
});
const featurePath = '/planning/projects/project/milestones/epic/features/feature';
const visible = async text => page.getByText(text, { exact: false }).first().waitFor({ state: 'visible' });
const screenshot = name => process.env.TEST_SCREENSHOTS ? page.screenshot({ path: `${output}/${name}.png`, fullPage: true }) : Promise.resolve();
const countRows = async count => {
  await page.waitForFunction(expected => document.querySelectorAll('.archive-flat .task-row').length === expected, count);
};
try {
  await page.goto(base + featurePath);
  await visible('Выполнено 2 из 8 задач');
  assert.equal(await page.locator('.planning-task-count').count(), 0);
  assert.equal(await page.getByRole('button', { name: 'Завершить фичу', exact: true }).count(), 0);
  assert.equal(await page.getByRole('button', { name: 'Готово · в архиве', exact: true }).count(), 0);
  assert.equal(await page.locator('.planning-feature-task').count(), 6);
  await page.getByRole('button', { name: 'Включить сортировку', exact: true }).click();
  assert.equal(await page.locator('.planning-order-row').count(), 6);
  assert.equal(await page.getByText('Фон 1', { exact: true }).count(), 0);
  assert.equal(await page.getByText('Фон 2', { exact: true }).count(), 0);
  await page.getByRole('button', { name: 'Готово', exact: true }).click();
  await screenshot('01-partial');

  // Completion hides rows regardless of their location or API status representation.
  tasks.forEach((task, i) => { task.location = ['planned', 'backlog', 'today', 'archived'][i % 4]; task.workStatus = ['Done', 'completed', 2, '2'][i % 4]; });
  await page.reload();
  await visible('Выполнено 8 из 8 задач');
  await visible('Нет невыполненных задач.');
  assert.equal(await page.locator('.planning-feature-task').count(), 0);

  tasks.forEach(task => { task.location = 'archived'; task.workStatus = 'done'; });
  tasks.push({ ...tasks[0], id: 'cancelled', title: 'Отменённый фон', workStatus: 'new' });
  await page.reload();
  await visible('Выполнено 8 из 8 задач');
  await visible('В архиве без выполнения: 1 — не учитываются');
  await screenshot('02-ready');
  rejectStatus = true;
  await page.getByRole('button', { name: 'Завершить фичу', exact: true }).click();
  await visible('Фича уже изменена. Обновите страницу.');
  assert.equal(feature.status, 'planned');
  rejectStatus = false;
  await page.getByRole('button', { name: 'Завершить фичу', exact: true }).click();
  await visible('✓ Фича завершена');
  assert.equal(feature.status, 'done');
  await screenshot('03-completed');
  await page.getByRole('button', { name: '← Назад', exact: true }).click();
  await visible('1 из 2 фич');
  await visible('50%');
  await page.goto(base + '/planning/projects/project');
  await visible('25%');
  await page.goto(base + featurePath);
  await page.getByRole('button', { name: 'Вернуть в работу', exact: true }).click();
  await page.getByRole('button', { name: 'Завершить фичу', exact: true }).waitFor();
  assert.equal(feature.status, 'active');

  await page.goto(base + '/tasks');
  await page.getByRole('button', { name: 'Сегодня', exact: true }).click();
  await page.getByRole('button', { name: 'Архив', exact: true }).click();
  await countRows(9);
  await page.getByRole('button', { name: 'Выполненные', exact: true }).click();
  await countRows(8);
  await screenshot('04-archive-done');
  await page.getByRole('button', { name: 'Без выполнения', exact: true }).click();
  await countRows(1);
  await screenshot('05-archive-unfinished');
  await page.locator('.archive-flat .task-row').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Удалить навсегда', exact: true }).click();
  await screenshot('06-delete-confirmation');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  assert.equal(writes.filter(write => write.method === 'DELETE').length, 0);
  await countRows(1);
  rejectDelete = true;
  await page.locator('.archive-flat .task-row').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Удалить навсегда', exact: true }).click();
  await page.getByRole('button', { name: 'Удалить навсегда', exact: true }).click();
  await page.getByRole('alert').filter({ hasText: 'Задача уже изменена' }).waitFor();
  await countRows(1);
  rejectDelete = false;
  await page.locator('.archive-flat .task-row').click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Удалить навсегда', exact: true }).click();
  await page.getByRole('button', { name: 'Удалить навсегда', exact: true }).click();
  await visible('В архиве нет задач с таким статусом.');
  assert.equal(tasks.length, 8);

  // Deleting a completed archived task also removes it from both counts.
  await page.getByRole('button', { name: 'Выполненные', exact: true }).click();
  await page.locator('.archive-flat .task-row').first().click({ button: 'right' });
  await page.getByRole('menuitem', { name: 'Удалить навсегда', exact: true }).click();
  await page.getByRole('button', { name: 'Удалить навсегда', exact: true }).click();
  await countRows(7);
  await page.goto(base + featurePath);
  await visible('Выполнено 7 из 7 задач');

  offlineWrites = true;
  await page.getByRole('button', { name: 'Завершить фичу', exact: true }).click();
  await visible('✓ Фича завершена');
  await visible('Нет сети. Статус сохранён и будет синхронизирован позже.');
  await page.reload();
  await visible('✓ Фича завершена');
  const pending = await page.evaluate(async () => {
    const db = await new Promise((resolve, reject) => { const request = indexedDB.open('personal-dashboard-v2'); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
    return new Promise((resolve, reject) => { const request = db.transaction('operations').objectStore('operations').getAll(); request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
  });
  assert.equal(pending.at(-1).payload.operation, 'setFeatureStatus');
  assert.equal(pending.at(-1).payload.featureStatus, 'done');
  assert.deepEqual(errors, []);
  console.log('PASS: progress, archive statuses, completion/reopen, epic/project percentages, conflict handling, filters, delete cancellation/success, offline completion.');
} finally { await browser.close(); }

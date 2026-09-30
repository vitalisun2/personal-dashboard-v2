// Production preview: npm run build && npm run preview -- --port 5190.
// Isolated profiles and intercepted APIs: never reads or writes the user's dataset.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium, webkit } from 'playwright-core';

const base = process.env.TEST_BASE_URL || 'http://127.0.0.1:5190';
const output = process.env.TEST_OUTPUT_DIR || 'test-results/tablet-layout';
await mkdir(output, { recursive: true });
const captures = [];
const checks = [];

async function fixtures(context) {
  const nodes = [
    { id: 'doc', kind: 'document', title: 'План недели', markdown: 'Работа с документом на планшете.\n'.repeat(45), parentId: null, position: 0, version: 1, path: 'План недели', archived: false },
    { id: 'other-doc', kind: 'document', title: 'Идеи для проекта', markdown: 'Следующие шаги', parentId: null, position: 1, version: 1, path: 'Идеи для проекта', archived: false },
  ];
  const feature = { id: 'feature', title: 'Планшетная версия', description: 'Адаптировать существующий интерфейс', position: 0, version: 1, status: 'planned' };
  const epic = { id: 'epic', title: 'Интерфейс приложения', description: 'Экраны и действия', position: 0, version: 1, features: [feature] };
  const project = { id: 'project', title: 'Personal OS', description: 'План развития', version: 1, isArchived: false, milestones: [epic] };
  const tasks = [
    { id: 'backlog-task', title: 'Проверить планшетный интерфейс', location: 'backlog', sectionId: 'section', position: 0 },
    { id: 'today-task', title: 'Проверить основные экраны', location: 'today', sectionId: 'section', position: 1 },
    { id: 'archive-task', title: 'Готовая задача', location: 'archived', sectionId: 'section', position: 2, workStatus: 'done' },
    { id: 'planned-task', title: 'Проверить формы и меню', location: 'planned', position: 3, projectId: project.id, milestoneId: epic.id, featureId: feature.id },
    ...Array.from({ length: 22 }, (_, i) => ({ id: `extra-${i}`, title: `Задача для прокрутки ${i + 1}`, location: 'backlog', sectionId: 'section', position: i + 4 })),
  ].map(task => ({ description: 'Описание задачи', version: 1, workStatus: 'new', ...task }));
  const sections = [{ id: 'section', name: 'Личное', location: 'backlog', isBacklogVisible: true, position: 0, version: 1 }];
  await context.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname;
    const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (path === '/api/v2/sync/state') return json({ epoch: 'tablet-layout-fixture' });
    if (path === '/api/v2/sync/changes') return json({
      changes: nodes.map((node, index) => ({ sequence: index + 1, snapshot: { type: 'knowledge.node', id: node.id, version: 1, payload: node, deleted: false } }))
        .filter(change => change.sequence > Number(url.searchParams.get('after'))),
      nextSequence: nodes.length, isComplete: true,
    });
    // Keep UI mutations in the real IndexedDB queue to verify local persistence.
    if (path === '/api/v2/sync/push') return json({ results: request.postDataJSON().operations.map(op => ({ operationId: op.operationId, applied: false, skipped: true })) });
    if (path === '/api/v2/planning/projects') return json([project]);
    if (path === '/api/v2/tasks') return json(tasks);
    if (path.startsWith('/api/v2/tasks/sections')) return json(sections);
    if (path.endsWith('/groups/order')) return json({ version: 0, keys: [] });
    if (path === '/api/v2/knowledge/tree') return json(nodes);
    if (path === '/api/v2/knowledge/search') return json([{ ...nodes[0], snippet: 'Работа с документом на планшете', updatedAt: '2026-10-01T10:00:00Z' }]);
    if (path === '/api/v2/search') return json({ hits: [{ source: { kind: 'knowledge.document', id: 'other-doc', title: nodes[1].title, path: nodes[1].path, snippet: 'Следующие шаги', version: 1, url: '/knowledge/other-doc' }, score: .7, semanticSimilarity: .7, matchKind: 'semantic' }], isComplete: true });
    if (path === '/api/v2/chat/conversations' && request.method() === 'POST') return json({ id: 'chat', title: 'Чат', area: 'knowledge', messages: [], turns: [] });
    if (path.endsWith('/turns') && request.method() === 'POST') {
      const body = request.postDataJSON();
      return json({ turn: { id: 'turn', userMessage: body.message, assistantMessage: 'Планшетный интерфейс готов к проверке.', scope: body.scope, requestedModel: 'Gemma', actualModel: 'Gemma', createdAt: '2026-10-01T10:00:00Z', sourceReferences: [] } });
    }
    if (path.startsWith('/api/v2/chat/') && request.method() === 'DELETE') return route.fulfill({ status: 204 });
    assert.fail(`Unmocked API: ${request.method()} ${path}`);
  });
}

async function queue(page) {
  return page.evaluate(() => new Promise((resolve, reject) => {
    const request = indexedDB.open('personal-dashboard-v2');
    request.onerror = () => reject(request.error);
    request.onsuccess = () => {
      const db = request.result;
      const tx = db.transaction('operations');
      const read = tx.objectStore('operations').getAll();
      read.onsuccess = () => resolve(read.result);
      read.onerror = () => reject(read.error);
      tx.oncomplete = () => db.close();
    };
  }));
}

async function eventually(check, message) {
  const deadline = Date.now() + 8000;
  while (Date.now() < deadline) {
    if (await check()) return;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  assert.fail(message);
}

async function fits(page, selector) {
  const { width, height } = page.viewportSize();
  await eventually(async () => {
    const rect = await page.locator(selector).boundingBox();
    return rect && rect.width > 0 && rect.height > 0 && rect.x >= -1 && rect.y >= -1 && rect.x + rect.width <= width + 1 && rect.y + rect.height <= height + 1;
  }, `${selector} settles inside ${width}×${height}`);
  const box = await page.locator(selector).boundingBox();
  assert.ok(box && box.width > 0 && box.height > 0, `${selector} is visible`);
  assert.ok(box.x >= -1 && box.y >= -1 && box.x + box.width <= width + 1 && box.y + box.height <= height + 1,
    `${selector} fits ${width}×${height}: ${JSON.stringify(box)}`);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'No page-wide horizontal overflow');
  return box;
}

async function capture(page, engine, state) {
  await page.evaluate(async () => {
    const finite = document.getAnimations().filter(animation => Number.isFinite(animation.effect?.getComputedTiming().endTime));
    await Promise.all(finite.map(animation => animation.finished.catch(() => {})));
  });
  const file = `${engine}-${state}.png`;
  await page.screenshot({ path: `${output}/${file}` });
  captures.push({ engine, state, file, viewport: page.viewportSize() });
}

for (const engine of (process.env.TEST_ENGINES || 'chromium,webkit').split(',')) {
  const browser = await (engine === 'webkit' ? webkit : chromium).launch(engine === 'webkit' ? { headless: true } : { channel: 'chrome', headless: true });
  const context = await browser.newContext({ viewport: { width: 1080, height: 810 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2, serviceWorkers: 'block' });
  const page = await context.newPage();
  page.setDefaultTimeout(8000);
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await fixtures(context);
  const nav = page.getByRole('navigation', { name: 'Навигация' });
  try {
    await page.goto(`${base}/knowledge`);
    await page.getByRole('button', { name: 'План недели', exact: true }).waitFor();
    const navBox = await fits(page, '.bottom-window');
    const bodyBox = await fits(page, '.app-body');
    assert.equal(navBox.x, 0);
    assert.equal(navBox.width, 200);
    assert.ok(bodyBox.x >= navBox.x + navBox.width, 'Sidebar and workspace do not overlap');
    for (const link of await nav.getByRole('link').all()) {
      const box = await link.boundingBox();
      assert.ok(box.height >= 44 && box.x + box.width <= 200, 'Sidebar links have readable touch targets');
    }
    await capture(page, engine, 'knowledge');

    await page.getByRole('textbox', { name: 'Поиск в базе знаний' }).fill('план');
    await page.locator('.results .result').first().waitFor();
    await page.getByText('Близость: 0,70', { exact: true }).waitFor();
    await capture(page, engine, 'knowledge-search');
    await page.getByRole('button', { name: 'Очистить поиск', exact: true }).click();
    await page.getByRole('button', { name: 'План недели', exact: true }).click();
    await fits(page, '#docFX');
    assert.ok(await page.locator('#docFX').evaluate(el => el.scrollHeight > el.clientHeight), 'Long documents scroll inside the workspace');
    await page.locator('.doc-open-title').click();
    await page.getByRole('textbox', { name: 'Название документа' }).fill('План недели на iPad');
    assert.equal(await page.getByRole('textbox', { name: 'Название документа' }).inputValue(), 'План недели на iPad');
    await page.getByRole('textbox', { name: 'Название документа' }).press('Enter');
    await page.locator('.doc-open-title').getByText('План недели на iPad', { exact: true }).waitFor();
    await capture(page, engine, 'document');
    await page.getByRole('button', { name: 'Назад', exact: false }).click();
    await page.getByRole('button', { name: 'Создать документ или раздел', exact: true }).click();
    await fits(page, '.sheet.open');
    await capture(page, engine, 'document-create');
    await page.locator('.sheet.open .name-field').fill('Новый документ iPad');
    await page.locator('.sheet.open').getByRole('button', { name: 'Создать', exact: true }).click();
    await page.locator('.doc-open-title').getByText('Новый документ iPad', { exact: true }).waitFor();
    assert.ok((await queue(page)).some(op => op.payload.title === 'Новый документ iPad'), 'Document creation is durable');
    await page.reload();
    await page.locator('.doc-open-title').getByText('Новый документ iPad', { exact: true }).waitFor();

    await nav.getByRole('link', { name: 'Планирование', exact: false }).click();
    await page.getByText('Интерфейс приложения', { exact: true }).waitFor();
    await capture(page, engine, 'planning');
    const epicRow = page.locator('.planning-milestone-row').first();
    await epicRow.click({ button: 'right', position: { x: 200, y: 20 } });
    await page.waitForFunction(() => {
      const menu = document.querySelector('.row-context-menu');
      return menu && menu.getBoundingClientRect().left >= 200;
    });
    const menuBox = await fits(page, '.row-context-menu');
    const headerBox = await page.locator('.topline').boundingBox();
    assert.ok(menuBox.x >= bodyBox.x && menuBox.y >= headerBox.y + headerBox.height,
      `Planning menu stays in the workspace below the header: ${JSON.stringify(menuBox)}`);
    await capture(page, engine, 'planning-menu');
    await page.getByRole('menuitem', { name: 'Изменить', exact: true }).click();
    await fits(page, '.sheet.open');
    await page.getByRole('textbox', { name: 'Название эпика', exact: true }).fill('Проверенный интерфейс');
    await page.locator('.sheet.open').getByRole('button', { name: 'Сохранить', exact: true }).click();
    await page.getByText('Проверенный интерфейс', { exact: true }).waitFor();
    await page.getByText('Проверенный интерфейс', { exact: true }).click();
    await page.getByText('Планшетная версия', { exact: true }).click();
    await page.getByText('Проверить формы и меню', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Добавить задачу', exact: true }).click();
    await fits(page, '.sheet.open');
    await page.getByRole('textbox', { name: 'Название задачи', exact: true }).fill('Проверить поворот экрана');
    await capture(page, engine, 'planning-create');
    await page.locator('.sheet.open .create-submit').click();
    await page.getByText('Проверить поворот экрана', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Включить сортировку', exact: true }).click();
    assert.equal(await page.locator('.planning-order-row').count(), 2);
    const orderedRows = page.locator('.planning-order-row');
    const before = await orderedRows.evaluateAll(rows => rows.map(row => row.dataset.orderId));
    const source = await orderedRows.first().locator('.planning-order-handle').boundingBox();
    const target = await orderedRows.last().boundingBox();
    await page.mouse.move(source.x + source.width / 2, source.y + source.height / 2);
    await page.mouse.down();
    await page.mouse.move(source.x + source.width / 2, target.y + target.height - 2, { steps: 8 });
    await page.mouse.up();
    await eventually(async () => JSON.stringify(await orderedRows.evaluateAll(rows => rows.map(row => row.dataset.orderId))) === JSON.stringify([...before].reverse()), 'Drag reorder changes the visible order');
    await page.locator('.planning-order-toggle[aria-pressed="true"]').click();
    await capture(page, engine, 'feature');

    await nav.getByRole('link', { name: 'Задачи', exact: false }).click();
    await page.getByRole('button', { name: 'Личное', exact: true }).click();
    await page.locator('[data-task-id="backlog-task"]').waitFor();
    await capture(page, engine, 'tasks');
    await page.getByRole('button', { name: 'Создать задачу или раздел', exact: true }).click();
    await fits(page, '.sheet.open');
    await page.locator('.sheet.open .name-field').fill('Новая задача iPad');
    await page.locator('.sheet.open textarea').fill('Проверка создания задачи');
    await capture(page, engine, 'task-create');
    await page.locator('.sheet.open').getByRole('button', { name: 'Создать', exact: true }).click();
    await page.locator('.task-title-open').getByText('Новая задача iPad', { exact: true }).waitFor();
    assert.ok((await queue(page)).some(op => op.type === 'tasks.task' && op.payload.title === 'Новая задача iPad'));
    await page.getByRole('button', { name: 'Назад', exact: false }).click();
    await page.locator('[data-task-id="backlog-task"]').dispatchEvent('pointerdown', { pointerId: 1, pointerType: 'touch', clientX: 700, clientY: 250, button: 0 });
    await page.getByRole('menu').waitFor();
    await fits(page, '.row-context-menu');
    await page.locator('[data-task-id="backlog-task"]').dispatchEvent('pointercancel', { pointerId: 1, pointerType: 'touch' });
    await capture(page, engine, 'task-menu');
    await page.locator('.heading').click();
    await page.locator('[data-task-id="backlog-task"] .task-open').click();
    await page.locator('.task-title-open').click();
    await page.locator('.task-title-detail-input').fill('Проверено на iPad');
    await page.locator('.task-title-detail-input').press('Enter');
    await page.getByRole('button', { name: 'Назначить фичу', exact: true }).click();
    await page.locator('.task-link-sheet select').nth(0).selectOption('project');
    await page.locator('.task-link-sheet select').nth(1).selectOption('epic');
    await page.locator('.task-link-sheet select').nth(2).selectOption('feature');
    await fits(page, '.task-link-sheet.open');
    await capture(page, engine, 'task-planning-link');
    await page.getByRole('button', { name: 'Оставить в Backlog', exact: true }).click();
    await page.locator('.task-detail-path').waitFor();
    await page.getByRole('button', { name: 'В Сегодня', exact: true }).click();
    await page.getByRole('button', { name: 'Новая', exact: true }).click();
    await page.getByRole('button', { name: 'В работе', exact: true }).waitFor();
    await capture(page, engine, 'task-detail');
    await page.getByRole('button', { name: 'В архив', exact: true }).click();
    await fits(page, '.app-confirm-card');
    await capture(page, engine, 'archive-confirm');
    await page.locator('.app-confirm-card').getByRole('button', { name: 'В архив', exact: true }).click();
    await eventually(async () => (await queue(page)).some(op => op.id === 'backlog-task' && op.payload.operation === 'archive'), 'Task archive persists');
    await page.getByRole('button', { name: 'Назад', exact: false }).click();
    await page.getByRole('button', { name: 'Сегодня', exact: true }).click();
    await page.getByRole('button', { name: 'Архив', exact: true }).click();
    await page.locator('.task-archive-title').waitFor();
    await capture(page, engine, 'archive');

    await page.getByRole('link', { name: 'Открыть чат с агентом', exact: true }).click();
    await nav.getByRole('link', { name: 'Агент', exact: false }).waitFor();
    await page.getByRole('textbox', { name: 'Сообщение агенту' }).fill('Проверь планшетный интерфейс');
    await page.getByRole('button', { name: 'Отправить', exact: true }).click();
    await page.getByText('Планшетный интерфейс готов к проверке.', { exact: true }).waitFor();
    await fits(page, '.chat-composer');
    await capture(page, engine, 'chat');
    await page.setViewportSize({ width: 1080, height: 430 });
    await fits(page, '.chat-composer');
    await fits(page, '.bottom-window');
    await capture(page, engine, 'keyboard-height');

    await page.setViewportSize({ width: 810, height: 1080 });
    await page.getByRole('button', { name: 'Назад', exact: false }).click();
    await fits(page, '.bottom-window');
    assert.ok((await nav.boundingBox()).y > 900, 'Portrait retains bottom navigation');
    await nav.getByRole('link', { name: 'База знаний', exact: false }).click();
    await capture(page, engine, 'portrait');
    await page.getByRole('button', { name: 'Назад', exact: false }).click();
    await page.getByRole('button', { name: 'Создать документ или раздел', exact: true }).click();
    await page.setViewportSize({ width: 1080, height: 430 });
    await fits(page, '.sheet.open');
    await capture(page, engine, 'form-keyboard-height');
    await page.locator('.sheet.open').getByRole('button', { name: 'Закрыть', exact: true }).click();

    await page.setViewportSize({ width: 393, height: 873 });
    await fits(page, '.phone-shell');
    await capture(page, engine, 'mobile');
    await page.getByRole('link', { name: 'Открыть чат с агентом', exact: true }).click();
    assert.equal(await nav.isVisible(), false, 'Mobile chat keeps navigation hidden');
    await fits(page, '.chat-composer');
    await page.getByRole('button', { name: 'Назад', exact: false }).click();
    await page.setViewportSize({ width: 1080, height: 810 });
    await nav.getByRole('link', { name: 'Тестирование', exact: false }).click();
    await page.getByText('Раздел для тестирования — пока не реализован.', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Настройки', exact: true }).click();
    await page.getByRole('radio', { name: 'Светлая', exact: true }).check();
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'light');
    await fits(page, '.appearance-dialog');
    await capture(page, engine, 'light-settings');
    await page.getByRole('button', { name: 'Закрыть настройки', exact: true }).click();
    await page.goto(`${base}/search`);
    await page.locator('.search-input input').fill('план');
    await page.locator('.result-group').first().waitFor();
    await fits(page, '.search-page');
    await capture(page, engine, 'search');
    assert.deepEqual(errors, [], 'No JavaScript runtime errors');
    checks.push({ engine, status: 'passed', scenarios: ['navigation', 'knowledge search', 'document edit/create/reload', 'planning menu/edit/hierarchy/create/drag reorder', 'task long press/create/edit/planning link/move/status/archive', 'chat send', 'keyboard height', 'portrait', 'mobile', 'settings/theme', 'search'] });
    console.log(`PASS ${engine}: tablet actions, persistence, layout, rotation, keyboard-height and mobile regression`);
  } catch (error) {
    await capture(page, engine, 'failure');
    console.error(JSON.stringify({ errors, url: page.url(), queued: (await queue(page)).map(op => ({ type: op.type, id: op.id, operation: op.payload?.operation, title: op.payload?.title })), menus: await page.locator('.row-context-menu').evaluateAll(items => items.map(el => ({ style: el.getAttribute('style'), rect: el.getBoundingClientRect().toJSON() }))) }, null, 2));
    throw error;
  } finally { await browser.close(); }
}
await writeFile(`${output}/manifest.json`, JSON.stringify({ checks, captures }, null, 2));

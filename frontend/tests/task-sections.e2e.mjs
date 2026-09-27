// Requires an isolated API/database in TEST_API_URL and a local Vite server.
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';
const api = process.env.TEST_API_URL;
assert.ok(api, 'Set TEST_API_URL to an isolated test API.');
const base = process.env.TEST_BASE_URL || 'http://127.0.0.1:5190';
const browser = await chromium.launch({ channel: 'chrome', headless: true });
async function create(path, body) {
  const response = await fetch(`${api}/api/v2/tasks${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  assert.ok(response.ok);
  return response.json();
}
try {
  for (const offline of [false, true]) {
    const title = `UI section ${crypto.randomUUID()}`;
    const section = await create('/sections', { name: title, location: 'backlog' });
    const task = await create('', { title: 'Last task', sectionId: section.id });
    const context = await browser.newContext({ serviceWorkers: 'block', viewport: { width: 393, height: 873 } });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.stack || error.message));
    await context.route('**/api/**', async route => {
      const request = route.request(), url = new URL(request.url());
      if (url.pathname.includes('/sync/') || (offline && request.method() !== 'GET')) return route.abort();
      const response = await route.fetch({ url: `${api}${url.pathname}${url.search}` });
      await route.fulfill({ response });
    });
    await page.goto(`${base}/tasks`);
    if (offline) {
      await page.getByRole('button', { name: title, exact: true }).click();
      await page.locator(`[data-task-id="${task.id}"]`).click({ button: 'right' });
      await page.getByRole('menuitem', { name: 'Убрать в архив', exact: true }).click();
      await page.getByRole('button', { name: 'В архив', exact: true }).click();
    } else {
      // Deleting a nonempty section archives its tasks; auto-removal must not cause a second DELETE/404.
      await page.locator(`[data-section-row-key="section:${section.id}"]`).click({ button: 'right' });
      await page.getByRole('menuitem', { name: 'Удалить раздел', exact: true }).click();
      await page.getByRole('button', { name: 'Удалить', exact: true }).click();
    }
    await page.locator(`[data-section-row-key="section:${section.id}"]`).waitFor({ state: 'detached' });
    if (!offline) assert.equal(await page.getByRole('alert').count(), 0);
    await page.reload();
    await page.getByRole('button', { name: 'Создать задачу или раздел', exact: true }).waitFor();
    // Wait for a successful data refresh (including reconciliation with pending offline operations).
    await page.waitForFunction(() => document.querySelector('.task-section-title'));
    assert.equal(await page.locator(`[data-section-row-key="section:${section.id}"]`).count(), 0);
    if (!offline) {
      await page.goto(`${base}/tasks/${task.id}`);
      await page.getByText(`Архив · ${title}`, { exact: true }).waitFor();
    }
    assert.deepEqual(errors, []);
    await context.close();
  }
  console.log('PASS: online section deletion avoids a second delete; offline last archive hides the group across reload; archive detail retains section name.');
} finally { await browser.close(); }

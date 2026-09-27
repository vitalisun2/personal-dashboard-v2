// Run only against an isolated API/database: TEST_BASE_URL=http://127.0.0.1:5191.
// This test creates, moves, archives and deletes its own tasks and sections.
import assert from 'node:assert/strict';
const base = process.env.TEST_BASE_URL;
assert.ok(base, 'Set TEST_BASE_URL to an isolated test API; never use the user database.');
const prefix = `section-test-${crypto.randomUUID()}`;
async function request(path, method = 'GET', body) {
  path = path.replace('location=backlog', 'location=Backlog').replace('location=today', 'location=Today');
  const response = await fetch(`${base}/api/v2/tasks${path}`, { method, headers: { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  assert.ok(response.ok, `${method} ${path}: ${response.status} ${response.ok ? '' : await response.text()}`);
  return response.status === 204 ? null : response.json();
}
const section = (name, location = 'backlog') => request('/sections', 'POST', { name: `${prefix}-${name}`, location });
const task = (sectionId, title = 'Task') => request('', 'POST', { title, sectionId });
const change = (item, action) => request(`/${item.id}/${action}`, 'POST', { expectedVersion: item.version });
const remove = item => request(`/${item.id}`, 'DELETE', { expectedVersion: item.version });
const exists = async (item, location) => (await request(`/sections?location=${location}`)).some(s => s.id === item.id);

for (const location of ['backlog', 'today']) {
  const draft = await section(`draft-${location}`, location);
  assert.ok(await exists(draft, location), 'intentionally empty sections remain');
  const source = await section(`source-${location}`);
  let first = await task(source.id, `${prefix}-first`);
  let second = await task(source.id, `${prefix}-second`);
  let group = source;
  if (location === 'today') {
    // Explicitly create a matching destination so that the move keeps its topic.
    group = await request('/sections', 'POST', { name: source.name, location });
    first = await change(first, 'today');
    assert.ok(await exists(source, 'backlog'), 'one remaining task keeps its group');
    second = await change(second, 'today');
    assert.equal(await exists(source, 'backlog'), false, 'last move removes source group');
  }
  const archived = await change(first, 'archive');
  assert.equal(archived.sectionId, null);
  assert.equal(archived.archivedSectionName, source.name);
  assert.ok(await exists(group, location), 'archiving one of two tasks retains group');
  const last = await change(second, 'archive');
  assert.equal(last.archivedSectionName, source.name);
  assert.equal(await exists(group, location), false, 'last archive removes group');
  assert.ok(await exists(draft, location), 'unrelated empty draft is retained');
  assert.equal((await request(`/${archived.id}`)).archivedSectionName, source.name);
  assert.ok(!(await request(`/groups/order?location=${location}`)).keys.includes(`section:${group.id}`));
  const restored = await change(archived, 'restore');
  assert.equal(restored.location, 'backlog');
  assert.ok((await request('/sections?location=backlog')).some(s => s.id === restored.sectionId));
  await remove(restored);
  await remove(last);
  // Direct permanent deletion also removes the last task's section.
  const directSection = await section(`direct-${location}`);
  let direct = await task(directSection.id);
  if (location === 'today') direct = await change(direct, 'today');
  const directGroup = { id: direct.sectionId };
  await remove(direct);
  assert.equal(await exists(directGroup, location), false);
}

// Moving a task between sections removes only its previous empty group.
const old = await section('old'), target = await section('target');
let moved = await task(old.id);
moved = await request(`/${moved.id}/section`, 'PUT', { expectedVersion: moved.version, sectionId: target.id });
assert.equal(await exists(old, 'backlog'), false);
assert.ok(await exists(target, 'backlog'));
await remove(moved);
console.log('PASS: Backlog/Today last archive, move, permanent delete, archive labels, restore, remaining tasks, explicit empty sections and group order.');

import assert from 'node:assert/strict';
import test from 'node:test';
import { conflictFields } from '../src/offline/conflictPresentation.ts';

const conflict = (type, local, server, extra = {}) => ({ type, id: 'item', title: 'Task', local, server,
  localDeleted: false, serverDeleted: false, operationIds: ['edit'], serverVersion: 7, ...extra });
const record = (type, id, payload) => ({ type, id, payload, deleted: false, version: 1 });

test('aliases, enum representations, timestamps and generated paths do not create differences', () => {
  const local = { title: 'Task', description: '', location: 1, workStatus: 1, projectId: 'project',
    isArchived: false, version: 2, position: 0, url: '/tasks/item' };
  const server = { Title: 'Task', Body: null, Placement: 'Backlog', WorkStatus: 'InProgress',
    Planning: { projectId: 'project' }, Archived: false, Version: 7, Position: 0, Path: 'Generated', UpdatedAt: 'later' };
  assert.deepEqual(conflictFields(conflict('tasks.task', local, server), []), []);
});

test('only changed fields are exposed and both sides use the same labels', () => {
  const fields = conflictFields(conflict('tasks.task',
    { title: 'Same', description: 'Same', location: 'today', workStatus: 1 },
    { title: 'Same', body: 'Same', placement: 'backlog', workStatus: 'done' }), []);
  assert.deepEqual(fields.map(field => field.label), ['Статус', 'Расположение']);
  assert.equal(fields[0].local, 'В работе'); assert.equal(fields[0].server, 'Готово');
  assert.equal(fields[1].local, 'Сегодня'); assert.equal(fields[1].server, 'Backlog');
});

test('hierarchy differences use names from nested project views rather than identifiers', () => {
  const entities = [record('planning.project.view', 'project', { title: 'Сайт', milestones: [
    { id: 'epic', title: 'Запуск', features: [{ id: 'feature', title: 'Оплата' }] },
  ] }), record('tasks.section.view', 'section', { name: 'Личные дела' })];
  const [field] = conflictFields(conflict('tasks.task',
    { location: 'today', projectId: 'project', milestoneId: 'epic', featureId: 'feature' },
    { placement: 'today', sectionId: 'section' }), entities);
  assert.equal(field.label, 'Расположение');
  assert.equal(field.local, 'Сегодня → Сайт → Запуск → Оплата');
  assert.equal(field.server, 'Сегодня → Личные дела');
});

test('knowledge moves compare parent IDs and reconstruct full parent paths', () => {
  const entities = [record('knowledge.node', 'root', { title: 'Работа', parentId: null }),
    record('knowledge.node', 'child', { title: 'Идеи', parentId: 'root' })];
  const [field] = conflictFields(conflict('knowledge.node',
    { title: 'Документ', markdown: 'Text', parentId: 'child', path: 'old path' },
    { title: 'Документ', markdown: 'Text', parentId: null, path: 'Документ' }), entities);
  assert.equal(field.local, 'База знаний → Работа → Идеи'); assert.equal(field.server, 'База знаний');
});

test('equal folder names with different IDs remain a visible location conflict', () => {
  const fields = conflictFields(conflict('knowledge.node', { parentId: 'one' }, { parentId: 'two' }),
    [record('knowledge.node', 'one', { title: 'Идеи' }), record('knowledge.node', 'two', { title: 'Идеи' })]);
  assert.equal(fields[0].key, 'location');
});

test('text changes stay expandable and deletions preserve the surviving text for copying', () => {
  const fields = conflictFields(conflict('knowledge.node', { title: 'Note', markdown: 'Unsynced text' }, {},
    { serverDeleted: true }), []);
  assert.equal(fields[0].server, 'Удалена');
  const text = fields.find(field => field.key === 'description');
  assert.equal(text.local, 'Unsynced text'); assert.equal(text.server, 'Удалено'); assert.equal(text.expandable, true);
});

test('archive, feature status and entity order differences use human labels', () => {
  const fields = conflictFields(conflict('planning.feature',
    { status: 1, isArchived: false, position: 0 }, { featureStatus: 'done', archived: true, position: 3 }), []);
  assert.deepEqual(fields.map(field => [field.label, field.local, field.server]), [
    ['Статус', 'В работе', 'Готово'], ['В архиве', 'Нет', 'Да'], ['Место в списке', '1', '4'],
  ]);
});

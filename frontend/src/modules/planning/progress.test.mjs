import assert from 'node:assert/strict';
import test from 'node:test';
import { epicProgress, projectProgress, featureTaskProgress } from './progress.ts';

const epic = (total, done) => ({ features: Array.from({ length: total }, (_, i) => ({ status: i < done ? 'done' : 'planned' })) });

test('feature progress retains completed tasks after archiving', () => {
  const tasks = Array.from({ length: 8 }, (_, i) => ({ location: i < 2 ? 'archived' : 'planned', workStatus: i < 2 ? 'done' : 'new' }));
  assert.deepEqual(featureTaskProgress(tasks), { completed: 2, total: 8, excluded: 0, canComplete: false });
});

test('unfinished archived tasks are excluded, not counted as completed', () => {
  assert.deepEqual(featureTaskProgress([
    { location: 'Archived', workStatus: 'New' },
    { location: 3, workStatus: 1 },
    { location: 'Archived', workStatus: 'Done' },
    { location: 'Today', workStatus: 'Done' },
  ]), { completed: 2, total: 2, excluded: 2, canComplete: true });
});

test('empty features and features containing only cancelled tasks cannot be completed', () => {
  assert.equal(featureTaskProgress([]).canComplete, false);
  assert.equal(featureTaskProgress([{ location: 'archived', workStatus: 'new' }]).canComplete, false);
});

test('restoring an unfinished task blocks completion; numeric statuses work', () => {
  const tasks = [{ location: 3, workStatus: 2 }, { location: '3', workStatus: '2' }];
  assert.equal(featureTaskProgress(tasks).canComplete, true);
  tasks[1] = { location: 'backlog', workStatus: 'new' };
  assert.deepEqual(featureTaskProgress(tasks), { completed: 1, total: 2, excluded: 0, canComplete: false });
});

test('project progress gives equal weight to epics with different feature counts', () => {
  assert.equal(projectProgress({ milestones: [epic(2, 2), epic(8, 0)] }), 50);
});

test('partially completed epics contribute to the project', () => {
  assert.equal(projectProgress({ milestones: [epic(2, 2), epic(2, 2), epic(2, 0)] }), 67);
  assert.equal(projectProgress({ milestones: [epic(2, 1), epic(2, 1), epic(2, 0)] }), 33);
  assert.equal(projectProgress({ milestones: [epic(2, 2), epic(2, 1), epic(2, 0)] }), 50);
});

test('empty projects and epics are at zero; empty epics still have weight', () => {
  assert.equal(projectProgress({ milestones: [] }), 0);
  assert.equal(epicProgress(epic(0, 0)), 0);
  assert.equal(projectProgress({ milestones: [epic(1, 1), epic(0, 0)] }), 50);
});

test('cached percentages are ignored and offline structural edits recalculate progress', () => {
  const project = { progressPercent: 20, milestones: [epic(2, 2), epic(8, 0)] };
  assert.equal(projectProgress(project), 50);
  project.milestones.pop();
  assert.equal(projectProgress(project), 100);
  project.milestones[0].features.push({ status: 'planned' });
  assert.equal(projectProgress(project), 67);
});

test('only completed features count for string and numeric API statuses', () => {
  assert.equal(epicProgress({ features: [2, '2', 'Done', 'done', 1, 'active', 0, 'planned'].map(status => ({ status })) }), 50);
});

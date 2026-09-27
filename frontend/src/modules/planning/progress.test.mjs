import assert from 'node:assert/strict';
import test from 'node:test';
import { epicProgress, projectProgress } from './progress.ts';

const epic = (total, done) => ({ features: Array.from({ length: total }, (_, i) => ({ status: i < done ? 'done' : 'planned' })) });

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

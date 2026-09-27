type EpicProgressSource = { features: ReadonlyArray<{ status: string | number }> }

export function featureTaskProgress(tasks: ReadonlyArray<{ location: string | number; workStatus: string | number }>) {
  const isDone = (status: string | number) => ['done', 'completed', '2'].includes(String(status).toLowerCase())
  const included = tasks.filter(task => !['archived', '3'].includes(String(task.location).toLowerCase()) || isDone(task.workStatus))
  const completed = included.filter(task => isDone(task.workStatus)).length
  return { completed, total: included.length, excluded: tasks.length - included.length, canComplete: included.length > 0 && completed === included.length }
}

export function epicProgress(epic: EpicProgressSource): number {
  const done = epic.features.filter(feature => ['done', '2'].includes(String(feature.status).toLowerCase())).length
  return epic.features.length ? Math.round(100 * done / epic.features.length) : 0
}

export function projectProgress(project: { milestones: ReadonlyArray<EpicProgressSource> }): number {
  const epics = project.milestones
  return epics.length ? Math.round(epics.reduce((total, epic) => total + epicProgress(epic), 0) / epics.length) : 0
}

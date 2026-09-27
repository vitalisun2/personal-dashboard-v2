type EpicProgressSource = { features: ReadonlyArray<{ status: string | number }> }

export function epicProgress(epic: EpicProgressSource): number {
  const done = epic.features.filter(feature => ['done', '2'].includes(String(feature.status).toLowerCase())).length
  return epic.features.length ? Math.round(100 * done / epic.features.length) : 0
}

export function projectProgress(project: { milestones: ReadonlyArray<EpicProgressSource> }): number {
  const epics = project.milestones
  return epics.length ? Math.round(epics.reduce((total, epic) => total + epicProgress(epic), 0) / epics.length) : 0
}

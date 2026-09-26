export type ReorderDrag = ReturnType<typeof startReorderDrag>

export function startReorderDrag(event: PointerEvent, row: HTMLElement, bounds?: () => DOMRect | undefined) {
  const rect = row.getBoundingClientRect()
  const pointerId = event.pointerId
  const offsetX = event.clientX - rect.left
  const offsetY = event.clientY - rect.top
  let ghost: HTMLElement | null = null
  let started = false
  ;(event.currentTarget as HTMLElement | null)?.setPointerCapture?.(pointerId)

  return {
    get started() { return started },
    update(next: PointerEvent) {
      if (next.pointerId !== pointerId) return false
      if (!started && Math.hypot(next.clientX - event.clientX, next.clientY - event.clientY) > 6) {
        started = true
        ghost = row.cloneNode(true) as HTMLElement
        ghost.classList.add('reorder-ghost')
        ghost.classList.remove('planning-order-source', 'task-drag-source', 'drag-source')
        ghost.textContent = ''
        const content = document.createElement('span')
        content.textContent = row.innerText.trim()
        content.style.opacity = '.2'
        ghost.append(content)
        ghost.style.width = `${rect.width}px`
        ghost.style.height = `${rect.height}px`
        document.body.append(ghost)
      }
      if (ghost) {
        const area = bounds?.()
        const pad = 4
        const left = next.clientX - offsetX
        const top = next.clientY - offsetY
        ghost.style.left = `${area ? Math.max(area.left + pad, Math.min(left, area.right - rect.width - pad)) : left}px`
        ghost.style.top = `${area ? Math.max(area.top + pad, Math.min(top, area.bottom - rect.height - pad)) : top}px`
      }
      return started
    },
    cleanup() { ghost?.remove(); ghost = null },
  }
}

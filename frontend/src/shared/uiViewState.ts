const prefix = 'personal-dashboard.view.'

export function readViewState<T>(key: string, fallback: T): T {
  try {
    const value = localStorage.getItem(prefix + key)
    if (!value) return fallback
    const parsed: unknown = JSON.parse(value)
    if (Array.isArray(fallback)) return Array.isArray(parsed) ? parsed as T : fallback
    if (fallback !== null && typeof fallback === 'object') {
      return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed as T : fallback
    }
    return typeof parsed === typeof fallback ? parsed as T : fallback
  } catch { return fallback }
}

export function writeViewState(key: string, value: unknown): void {
  try { localStorage.setItem(prefix + key, JSON.stringify(value)) }
  catch { /* Private browsing or full device storage. */ }
}

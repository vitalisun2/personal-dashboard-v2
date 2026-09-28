export type Theme = 'light' | 'dark'

export const themeStorageKey = 'personal-os-theme'

export function readTheme(): Theme {
  try {
    return localStorage.getItem(themeStorageKey) === 'light' ? 'light' : 'dark'
  } catch {
    return 'dark'
  }
}

export function applyTheme(theme: Theme) {
  document.documentElement.dataset.theme = theme
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'light' ? '#ffffff' : '#212121')
}

export function saveTheme(theme: Theme) {
  applyTheme(theme)
  try {
    localStorage.setItem(themeStorageKey, theme)
  } catch {
    // The appearance still changes when browser storage is unavailable.
  }
}

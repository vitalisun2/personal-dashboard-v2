# Tablet layout

The existing screens, routes, editing controls and local persistence are reused.
Only the shell and presentation adapt; lists and details keep their existing
navigation flow. No extra master/detail state or duplicate module components.

- Below 768 CSS pixels: existing mobile layout.
- From 768 CSS pixels: full-window, single workspace with bottom navigation.
- From 960 CSS pixels: 200-pixel sidebar and a workspace capped at 860 pixels
  including its horizontal padding. This covers iPad 9 landscape at 1080 × 810.
- Breakpoints use width, so opening a keyboard does not change the navigation
  layout merely because the available height changes.
- Tablet forms reuse the mobile sheets, centered and capped at 520 pixels. They
  scroll within the visible viewport; reading and editing text uses 16 pixels.
- Existing light/dark tokens, safe areas and VisualViewport handling are reused.

The sidebar follows [Apple's navigation guidance](https://developer.apple.com/design/human-interface-guidelines/sidebars).
The sidebar remains accessible in Chat. Planning menus are bounded by the workspace,
not by the position of the navigation bar. Fixed task menus no longer inherit a
zero-distance pull-to-refresh transform. Knowledge cache reads cannot replace a
newer local edit with an older read result; this was exposed by the WebKit run.

## Verification

Use Node.js 24 from `frontend/`:

```powershell
npm run build
npm run preview -- --host 127.0.0.1 --port 5190 --strictPort
# In another terminal:
node node_modules/playwright-core/cli.js install webkit
$env:TEST_OUTPUT_DIR = 'test-results/tablet-layout'
node tests/tablet-layout.e2e.mjs
```

The harness uses isolated browser profiles and intercepts every API request. It
checks Chromium and WebKit with touch/mobile emulation at 1080 × 810, portrait
810 × 1080, phone 393 × 873 and reduced keyboard-height viewports. Scenarios cover
navigation, search, creation, editing, reload persistence, planning hierarchy and
drag reorder, long-press menus, task links/status/moves/archive, chat and themes.
Screenshots and `manifest.json` describe the completed states. Keyboard-height
emulation and desktop WebKit do not replace a final check on physical iPad Safari.

Existing sync/progress/chat unit tests and entity/order-conflict browser suites
are also applicable. The older `planning-reorder.e2e.mjs` and `offline-first.mjs`
fail on both this change and the unchanged `ad30bcc` baseline: the former expects
completed tasks to be excluded from the current order list, and the latter uses
an outdated sections fixture/store expectation. Those scripts were left unchanged.

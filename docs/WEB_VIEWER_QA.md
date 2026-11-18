# Web viewer QA — 2026-11-18

Test input was the synthetic-only `tools/StreamingProbe` host, with no saved
settings or firewall changes. JPEG and hardware H.264 WebRTC were both exercised
in Chrome 157 on Windows. Mobile sizes used browser emulation, not physical phones.

## Executed checks

- .NET regression suite: **105 passed**. JavaScript suite: **23 passed**.
- Viewer JavaScript syntax valid; Release builds/publish completed successfully.
- HTTP document, stylesheet and script returned 200. The final browser session
  had no error/warning console messages, including after fullscreen exit.
- Layout checked at 320×568, 390×844, 768×1024, 844×390 and 1440×900. The fitted
  stream stayed inside its viewport with its original aspect ratio. Visible
  controls stayed within the screen at these sizes.
- Native fullscreen collapsed both bars, leaving exactly one visible control.
  Expanding the header preserved the video rectangle. Controls collapsed after
  idle and after clicking the stream. Keyboard focus prevented premature hiding.
- Browser tests also disabled fullscreen APIs to exercise simulated fullscreen,
  H to reveal controls and Escape to restore the regular view.
- Audio dialog: initial focus, Tab wrap, Escape and return focus checked.
  PIN challenge: input focus, wrong-PIN error state, Tab wrap, successful
  authentication and video reconnection checked with a synthetic test PIN.
- WebRTC video decoded at 1920×1080. Its inactive canvas was hidden; in JPEG mode
  the inactive video was hidden. Zoom, Fit to screen and screenshot dimensions
  were checked during WebRTC playback.

Before the fix, the extra empty video element shifted the JPEG canvas 150 CSS
pixels left at desktop and phone sizes. After the fix its fitted left edge was
zero in the portrait test, with the full source visible. At 844×390 fullscreen,
the 16:9 WebRTC image occupied the full height, centered between black sidebars.

## Accessibility review and limits

Used `frontend-design`, `ecc:frontend-a11y` and `browser-qa`. Axe-core 4.14.1 was
injected only into the isolated test browser; it is not a production dependency.
It reported **zero violations** for the selected WCAG A/AA checks in normal
light/dark views, audio diagnostics and collapsed/expanded fullscreen.

Axe marked contrast of some changing/translucent toolbar content and video
captions for manual review. Those incomplete results are not a pass. The toolbar
uses pale text over a dark overlay in both themes; focus/labels and keyboard
behavior were also reviewed separately. Live captioning was not added or
certified by this UI change. There was no physical iOS/Android or screen-reader
validation, and safe-area behavior around real notches remains device testing.

Formal visual regression: **INCONCLUSIVE**, because the repository has no
committed screenshot baseline. Manual before/after screenshots and layout
measurements were reviewed. Local preview: `build/web-ui-preview/` (ignored).
No new FPS benchmark is claimed for these layout changes.

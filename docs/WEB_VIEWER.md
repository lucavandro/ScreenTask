# Web viewer controls

The header contains fullscreen and theme actions. The GitHub shortcut has been
removed from the viewer header.

## Fullscreen

Entering fullscreen hides the header and leaves a compact 44-pixel control in
the top-right corner. Activate it to show or hide the header and playback
controls. The opened header overlays the stream instead of moving it. Fullscreen
and theme actions remain available there. **H** also toggles the controls; it does not intercept text
entry. **Esc** exits simulated fullscreen; native fullscreen also responds to
the browser's own exit controls.

The same header behavior applies to native Fullscreen API, the prefixed WebKit
API and the existing simulated fullscreen fallback. Exiting fullscreen restores
the ordinary header. Hidden actions leave the tab order; focus moves to the
small toggle when its focused header action is hidden, and back to the
fullscreen action when that toggle disappears.

Fullscreen controls collapse after five seconds of inactivity. Hover, keyboard
focus inside the controls, and open dialogs keep them available. Tapping/clicking
the stream closes the controls immediately. Mouse movement alone does not reveal
them again. Keyboard activation opens with focus on the fullscreen action.
The optional audio reminder and zoom badge stay out of the way when the controls
are closed. Announcements, connection errors and PIN prompts remain available.

## Stream layout and accessibility

Only the active JPEG canvas or WebRTC video surface participates in layout. The
inactive surface is hidden, fixing the previous off-center crop caused by an
empty video element alongside the canvas. The stream fits the available area
while preserving its aspect ratio. Opening fullscreen controls never changes
its dimensions. Black margins are expected when the source and viewport differ.
Fullscreen also respects device safe-area insets around notches and home bars.

On narrow screens, stream information and playback actions wrap into separate
rows. Volume and screenshot controls remain reachable, including while zoomed.
**Fit to screen** resets zoom and pan; the existing **0**, **F**, **M** and **S**
shortcuts remain available outside form fields and dialogs.

Controls have at least 44-pixel touch targets, visible keyboard focus, labels and
expanded/pressed states. PIN and audio dialogs have names and contain Tab focus;
audio diagnostics closes with Escape and restores focus to its opener. PIN error
state is announced. Reduced-motion preferences disable decorative animation.
The playback toolbar uses a consistent dark palette over the stream in either
theme; connection/error panels follow the selected light or dark theme.

Changes to embedded viewer assets require rebuilding the application. The
fullscreen state tests run with:

```powershell
node -c ScreenTask/WebServer/viewer.js
node --test ScreenTask.Tests/Web/*.test.cjs
```

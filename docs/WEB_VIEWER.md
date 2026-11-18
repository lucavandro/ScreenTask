# Web viewer controls

The header contains fullscreen and theme actions. The GitHub shortcut has been
removed from the viewer header.

## Fullscreen

Entering fullscreen hides the header and leaves a compact 44-pixel control in
the top-right corner. Activate it to show or hide the header. The opened header
overlays the stream instead of moving it. Fullscreen and theme actions remain
available there. **H** also toggles the controls; it does not intercept text
entry. **Esc** exits simulated fullscreen; native fullscreen also responds to
the browser's own exit controls.

The same header behavior applies to native Fullscreen API, the prefixed WebKit
API and the existing simulated fullscreen fallback. Exiting fullscreen restores
the ordinary header. Hidden actions leave the tab order; focus moves to the
small toggle when its focused header action is hidden, and back to the
fullscreen action when that toggle disappears.

Changes to embedded viewer assets require rebuilding the application. The
fullscreen state tests run with:

```powershell
node -c ScreenTask/WebServer/viewer.js
node --test ScreenTask.Tests/Web/*.test.cjs
```

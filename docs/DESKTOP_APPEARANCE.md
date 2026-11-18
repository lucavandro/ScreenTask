# Desktop appearance and JPEG quality

## Image Quality

The slider feeds GDI+'s JPEG `Encoder.Quality` parameter (20–100 in the UI).
It is a lossy quality setting, not a percentage of the original file size.
Lower quality usually reduces encoded frame size by discarding more detail.
It can relieve encoder/network pressure, but does not change capture resolution,
capture cost or the target FPS. The result depends on screen content and the
current bottleneck; an FPS increase is not guaranteed.

The current streaming protocol remains JPEG with binary packet type `0x01`.
WebP was removed after the earlier comparative benchmarks. A screenshot that
still shows a WebP selector belongs to an older build.

## Windows theme

At startup, `DesktopTheme.Initialize()` selects WinForms' native Windows app
color mode before any controls are created. Windows 11 follows the Windows
light/dark app preference. Windows 10 retains the classic supported palette.
High Contrast retains the system palette and native tab rendering.

The .NET 9 color-mode API is experimental; the WFO5001 suppression is confined
to that single startup call. There are no new dependencies or Windows preference
writes. Changing the Windows theme requires restarting ScreenTask because the
.NET 9 API does not dynamically switch existing controls.

Explicit fixes cover read-only text backgrounds, status-strip gradients and
tab chrome. Native tab selection, keyboard navigation and accessibility remain
provided by `TabControl`. Feedback status colors and links adapt to the palette.
The confidence monitor stays black and the pairing QR stays black on white.

Sources:
- [Microsoft: .NET 9 WinForms dark mode](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/whats-new/net90#dark-mode)
- [Microsoft: SetColorMode limitations](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.application.setcolormode)

## Application icon

`ScreenTask/Assets/ScreenTask.png` is the generated master. `ScreenTask.ico`
contains 16, 20, 24, 32, 40, 48, 64, 128 and 256-pixel frames. The current icon
uses a circular blue-to-cyan badge with an ivory monitor and a simplified
broadcast symbol. The outer corners are transparent so the silhouette remains
circular; the badge and glyphs use filled colors without transparent cutouts.
A light blue perimeter keeps the silhouette readable on light and dark themes.
The executable, main window and notification-area icon use the same ICO.
The original legacy ICO files are retained.

Created with the built-in **imagegen** tool. Design brief: a modern circular
screen-sharing badge, a simple rounded monitor, two bold broadcast arcs and a
dot, a restrained cobalt-to-teal palette, no lettering, no outer shadow and
transparency only outside the circular badge. The previous icon supplied the
screen-sharing motif as a reference.

Regenerate the Windows ICO from the PNG master on Windows without extra tools:

```powershell
./scripts/Build-AppIcon.ps1
```

The script resamples the master into nine PNG-backed 32-bit ICO entries and
preserves alpha for Windows 10/11. Rebuild the application afterward because
both the executable and WinForms resources embed the ICO at build time.

## Verification

- Release build: zero warnings/errors.
- Existing .NET regression suite and JavaScript decoder tests.
- Separate STA preview processes for Classic, Dark and System color modes on
  Windows 11, without changing the user's Windows theme or saved app settings.
- Main Broadcast/Viewers/Settings tabs, source picker and feedback form rendered
  for inspection. Real on-screen capture additionally checked title-bar styling.
- QR background and form/tray icons checked during preview; ICO dimensions and
  transparent corners and filled centers checked in the ICO frames.

Local preview harness and screenshots: `build/theme-preview/` (ignored build
artifacts). No new screen capture or encoding benchmark is needed for these UI
changes: they do not alter the capture/encoding pipeline.

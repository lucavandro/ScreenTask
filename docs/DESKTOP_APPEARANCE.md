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
contains 16, 20, 24, 32, 40, 48, 64, 128 and 256-pixel frames. Every frame is
fully opaque, with a filled monitor and broadcast mark on a muted blue tile.
The executable, main window and notification-area icon use the same ICO.
The original legacy ICO files are retained.

Created with the built-in **imagegen** tool. Final generation prompt:

> Use case: logo-brand. Create one finished square Windows desktop application icon for ScreenTask, an offline local-network screen-sharing app. A simple filled computer monitor with a small three-arc broadcast mark integrated into its screen, bold enough to recognize at 16 pixels. Flat precise modern geometric shapes, front view, centered, generous but not excessive margins. Opaque muted medium blue square tile background covering the entire canvas edge to edge, pale cool ivory filled monitor silhouette, a darker slate-blue inset display and pale blue broadcast mark. Restrained soft contrast, solid fills, very few details, thick clean silhouette. Must remain clearly visible on both dark and light Windows taskbars, without depending on background transparency. No letters, no words, no numbers, no glass, no transparency, no tiny strokes, no 3D perspective, no glows, no mockup, no extra backdrop outside the icon tile. Asset is the icon itself, not a presentation of an icon. High resolution square PNG.

## Verification

- Release build: zero warnings/errors.
- Existing .NET regression suite and JavaScript decoder tests.
- Separate STA preview processes for Classic, Dark and System color modes on
  Windows 11, without changing the user's Windows theme or saved app settings.
- Main Broadcast/Viewers/Settings tabs, source picker and feedback form rendered
  for inspection. Real on-screen capture additionally checked title-bar styling.
- QR background and form/tray icons checked during preview; ICO dimensions and
  full opacity checked in every frame.

Local preview harness and screenshots: `build/theme-preview/` (ignored build
artifacts). No new screen capture or encoding benchmark is needed for these UI
changes: they do not alter the capture/encoding pipeline.

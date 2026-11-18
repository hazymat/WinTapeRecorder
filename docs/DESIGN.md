# Design rules

Shared by WinSqueeze, WinTapeRecorder, WinGCal and WinSwitch. This file is identical in all four repos: change it in all four at once. Where an app's SPEC.md disagrees, this file wins; bring the SPEC in line.

## Tray notifications

Apps without a tray icon (WinTapeRecorder today) follow the same colours, cards and click rule for any dot in their own window.

The one rule: **no colour, dot or indicator ever appears whose meaning isn't shown straight away by clicking it.** Every coloured dot on the tray icon leads, in one click, to a card of the same colour, and has a matching row of the same colour in the right-click menu.

### The dots

| Dot | Colour | Means | Flashes |
|---|---|---|---|
| Blue | `#4CC2FF` | An update is downloaded and ready | Until seen (left-click, or the right-click menu opened), then solid until the app is updated |
| Amber | `#E0912F` | A message needs attention (a question closed with ×, a warning; WinGCal: an upcoming event, until it starts) | The whole time the message waits |

- Flash timing: 900 ms on, 500 ms off. Dots in the right-click menu flash while the menu is open.
- One dot on the icon at a time: amber wins over blue.
- Both are optional: Settings › General has a switch for each (on by default). Off means no dot on the icon; the menu row and the card still exist.
- No other colours on or by the tray icon. Profile, calendar and other user-picked colours must never be amber or the update blue (or anything close: no orange), so a dot can never be mistaken for one. Amber and blue are reserved app-wide.

### Hover

The tooltip starts with the dot and its meaning, before anything else: `🟠 <message title>` or `🔵 Update ready: x.y.z`, then the usual text on the next line.

### Left-click

With a dot showing, left-click opens the matching card instead of the app's usual left-click action:
- Amber: the message card. Shown even during Do not disturb or a full-screen app, because it was asked for.
- Blue: the update card, every time (seen or not), while an update is ready.
- Amber first if both are waiting; the next click shows the blue one.

Without a dot, left-click does the app's normal thing.

### The cards

The app's own glass cards just above the tray clock, never Windows toasts. Each card carries its colour in three places: the header dot, the primary button and the edge. Header: `<App> · Message` or `<App> · Update`.
- Update card: the new version, one line of what's new, and a blue primary button **"Update <App> now"**. Never "Restart" or "Restart to update" (reads as restarting Windows).
- Message card: the message, its own buttons (the primary one amber), ×. Closing it with × keeps the message (and the dot) waiting; only answering it clears it.
- Plain information cards (no dot behind them) use a neutral or green dot, never amber or blue.

### Right-click menu

Always carries the matching rows, at the top, each with its dot in the same colour:
- `Show message` (amber), while a message waits.
- `Update <App> to x.y.z` (blue), while an update is ready.

They are there even when the dots are switched off in Settings.

### Tray icon

- Bold line art on a transparent background, filling the whole square slot (Windows gives apps a square slot only).
- Snapped to whole pixels at every size Windows asks for (16, 20, 24, 32 px and up): crisp lines, no half-pixel blur. Draw per size, not scaled from one bitmap.
- Follows the taskbar's light or dark theme.
- The dot sits in a fixed corner with a clear ring cut around it, so it never merges into the drawing.

### Checklist before shipping anything with a colour

1. Click it: does the meaning appear straight away?
2. Is there a menu row of the same colour?
3. Is the colour amber or update blue? Then it must mean exactly what this file says.

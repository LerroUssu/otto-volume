<p align="center"><img src="docs/banner.svg" alt="Otto" width="100%"></p>

# Otto

*Formerly **Fader**. Named after Otto Suwen from Re:Zero, the merchant whose blessing lets him hear every voice around him; the green comes from his cloak. Updating from Fader keeps your settings and renames `Fader.exe` to `Otto.exe` by itself.*

Per-app volume for Windows 10/11 that is actually quick to use, plus a start level that stops
apps from launching too loud. One hand on the mouse, no key held, nothing to install.

**[Download the latest release](https://github.com/LerroUssu/otto-volume/releases/latest)** · [Pretty overview page](https://lerroussu.github.io/otto-volume/) · MIT

## Get it

- `dist\portable\Otto.exe` — **runs on any Windows 10/11 x64 PC**, nothing to install (the .NET
  runtime is inside; ~70 MB). Copy it anywhere. Put an empty file named `portable` (or a
  `settings.json`) next to it and Otto keeps its settings and log in that folder instead of
  `%APPDATA%\Otto` — handy on a USB stick.
- `dist\Otto.exe` — small build (~1 MB) for PCs that already have the .NET 8 Desktop runtime.
- Unsigned: on another PC Windows SmartScreen may say "Windows protected your PC" the first time;
  choose More info → Run anyway.

## Use it

Run `Otto.exe`. It lives in the tray (no window). Then:

| Do this | Result |
| --- | --- |
| Left-click the tray icon, or press **Ctrl+Alt+V** | The mixer flyout: every app with audio, icon (click = mute), slider, live meter, master at the top. Click anywhere else or press Esc to close. Scroll over a slider to nudge it. |
| **Scroll the wheel over an app's taskbar button** (no key) | Changes that app's volume; a pill above the button shows the level (it also appears when you just hover the button). Over the clock, tray icons or empty taskbar it changes the master volume. |
| **Click the normal Windows speaker icon** in the tray | Opens Otto's mixer instead of the Windows slider (right-click on the icon is still Windows'). |
| Hold **Alt** and scroll the mouse wheel over any window | Changes *that app's* volume, with a small pill showing the level. Works over borderless full-screen games. Over the desktop it changes the master volume. |
| **Drag the mixer** anywhere (or click its pin) | It stays open until you press Esc, click the pin, or toggle it from the tray. |
| **Level pill position** (Settings) | The pill always appears at the same spot: top, center or bottom of the screen the mouse is on (bottom by default). |
| Launch an app that plays sound | The moment Windows creates its audio session, Otto sets it to the **start level** (50 % by default). Per-app rules give some apps their own start level or leave them alone. |
| **Slide past 100 %** (an app, or the master for every app) | Boost: the app gets louder than Windows allows, up to 200 % (Settings: 150 / 200 / 300 %). Needs a spare output, see [Boost](#boost). |
| Right-click the tray icon | Open mixer, Settings, Start with Windows, Check for updates, Quit. When a new version is out, **Update to vX** appears at the top. |

Settings (gear in the flyout, or the tray menu) let you change the modifier (Alt / Ctrl+Alt /
Shift+Alt / Ctrl+Shift), the wheel step, the hotkey, the default start level, and
per-app rules (e.g. Chrome starts at 40 %, Spotify is left alone).

Command line: `Otto.exe --quit` asks a running Otto to exit cleanly; `Otto.exe --autostart`
starts it and switches "start with Windows" on; `Otto.exe --update-now` starts it and installs the
latest release right away if there is one.

### Updates

Otto checks this repo's [releases](https://github.com/LerroUssu/otto-volume/releases) 45 s after it
starts and then twice a day (one anonymous GitHub API call, nothing sent; turn it off in
Settings → Updates). When a newer version exists, a notification says so and the tray menu gets
**Update to vX**. Clicking it downloads the matching exe (portable builds update to the portable
exe, small builds to the small one) next to the current one, checks its size and SHA-256 against
GitHub, renames the running exe to `Otto.exe.old`, moves the new one in and restarts. The `.old`
file is deleted on the next start. Settings and "start with Windows" are untouched. If the folder
is read-only, it says so and the release page has the download.

### Boost

Windows' per-app and master volumes stop at 100 %. To go further, Otto needs a **spare output**
nobody listens to: [VB-CABLE](https://vb-audio.com/Cable/) (free), or Voicemeeter's
"VoiceMeeter Input" (turn it on in Sound settings → More sound settings → Playback → right-click →
Show disabled devices → Enable; don't make it the default). Otto picks a virtual cable by itself;
Settings → Boost lets you choose another output.

When an app goes past 100 % Otto sends it to the spare output (the same per-app setting as
Windows' *App volume and device preferences*), mutes that output, records the app there
(process loopback) and plays it × the boost on your real output through a limiter. Back at
100 % the app returns to its usual output and Otto's stream closes. The master slider past
100 % does this for every app. A boosted app is about 30 ms late; nothing changes below 100 %.

Apps that pick a fixed device themselves (some games) don't follow the move: Otto notices
within a few seconds, puts the app back and keeps it at 100 %. Windows remembers per-app
outputs, so if Otto is killed while boosting it puts the apps back the next time it runs.

### Volume vs loudness

The start level only sets an app's volume *multiplier*. They cannot stop a video
that is mastered three times louder than the voice chat next to it. A per-app leveler was
tried and removed: Windows' per-app meter reads the signal before the app's volume and, for
Chrome, barely moves between speech and a loud video, so it cannot tell the two apart. If you
want everything flattened, Windows has a system-wide "Loudness Equalization" enhancement in the
output device's properties (Sound → device → Properties → Enhancements).

### Tray icon

Windows hides new tray icons behind the ^ overflow arrow. Drag Otto's icon from there onto the
taskbar once and it stays. With "clicking the speaker icon opens Otto" on (default) you rarely
need it anyway.

### How "start level" decides when to act

An app gets the start level when its audio session appears **and** either its process is
younger than 60 s (it was just launched) or the app is not in the mixer at all (its previous
process is gone) and has been gone for at least 10 minutes. An app that merely pauses and
resumes keeps its level, the same as in the Windows mixer. If you set a level by hand during
the current Otto run, that app is left alone unless it is genuinely relaunched. Sessions that
already existed when Otto started, and sessions re-created because the default output device
changed, are never reset. Windows itself remembers the last level per
app, so everything Otto sets persists like a manual change in the Windows mixer.

## Files

- Settings: `%APPDATA%\Otto\settings.json` (plain JSON, editable while Otto is closed)
- Log: `%APPDATA%\Otto\otto.log`
- "Start with Windows" writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Otto` only when you turn it on.

To uninstall: quit Otto, turn off "Start with Windows" first if you enabled it, delete the
folder and `%APPDATA%\Otto`.

## Build

Needs the .NET 8 SDK (this machine has a user-local one in `%LOCALAPPDATA%\Microsoft\dotnet`).

```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

Headless self-test (opens no window, changes nothing): `Otto.exe --probe report.json`.


## How it works

- Windows Core Audio session API through NAudio.Wasapi: `IAudioSessionManager2` to enumerate
  sessions and get `OnSessionCreated`, `ISimpleAudioVolume` for level/mute,
  `IAudioMeterInformation` for the meters (measured *before* the session volume, verified: halving
  the gain leaves the reading unchanged),
  `IAudioSessionEvents` for external changes, `IMMNotificationClient` to follow the default device.
- Sessions are grouped per executable name (Chrome's audio lives in a helper `chrome.exe`
  process, Discord has two sessions): one row per app, the window under the cursor maps to
  the same key.
- `WH_MOUSE_LL` hook for the modifier + wheel gesture (the wheel event is swallowed so the
  app underneath does not zoom); an unassigned key press is injected so releasing Alt does
  not focus the app's menu bar. `RegisterHotKey` for the flyout. UI Automation to name the
  hovered taskbar button.
- Icons from `IShellItemImageFactory` (the high-res icons Explorer uses), fallbacks to
  `ExtractIconEx` and the session's own icon path (system sounds).
- UI: WPF, transparent rounded windows, Figtree (OFL), green accent, Segoe MDL2 glyphs.

## Limits

- Apps running **as administrator** do not receive the Alt+scroll gesture unless Otto itself
  runs elevated (Windows blocks lower-integrity hooks). The flyout still controls them.
- Exclusive full-screen (old DirectX) games hide every overlay, so no pill there; borderless
  is fine. The volume still changes.
- Taskbar-button matching goes by button name / window title; an app whose window title
  contains neither its name nor a known window title falls back to the master volume.
- Only the default output device is shown (what the Windows mixer shows too).
- Boost needs a spare (virtual) output, and an app must follow default-device changes to be
  boosted (Chrome, Discord, Spotify, most players do).

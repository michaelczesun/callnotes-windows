# CallNotes for Windows — Architecture Contract

Status: **draft contract, v0.1** — this document is the single source of truth for
the Windows port ("callnotes-windows"). It defines project layout, shared file
formats, CLI contracts, the exact Win32/COM interop needed, and what v1 explicitly
does not do. Everything here must stay in lockstep with the macOS original at
`/Users/michaelczesun/Documents/callnotes` (same config keys, same state-file
shapes, same processing pipeline stages) so that documentation, mental model, and
support answers apply to both siblings without translation.

License: PolyForm Noncommercial License 1.0.0, Copyright (c) Michael Czesun.
UI: German/English, same `L(de, en)` pattern as the Mac app (system locale +
`uiLanguage` config override, `"system" | "de" | "en"`).

---

## 1. Why this needs its own recorder core (not a straight port)

The Mac app's system-audio capture is Core Audio **Process Taps** (macOS 14.2+,
`CATapDescription`, `AudioHardwareCreateProcessTap`) — a macOS-only API family with
no Windows equivalent. Windows has a different, but conceptually parallel,
capability since **Windows 10 Build 20348 (20H1 SDK) / practically Build 20438+**:
**WASAPI Process Loopback Capture** via `ActivateAudioInterfaceAsync` with
`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`. It captures only the render
(output) audio produced by one process (+ its child-process tree), with **no
virtual audio driver, no system-wide loopback, no Stereo-Mix hack**. This is the
Windows-native analogue to the Mac's per-app Process Tap and is the reason a
from-scratch C# recorder core ("calltap") is required; everything downstream
(Python pipeline, config schema, state files, note format) is reused unchanged
from the Mac project.

Sources (verified against Microsoft Learn, current as of this writing):
- `AUDIOCLIENT_ACTIVATION_PARAMS` struct, min. supported client **Windows 10 Build 20348**:
  https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_activation_params
- `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS` struct (`TargetProcessId`, `ProcessLoopbackMode`):
  https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params
- `PROCESS_LOOPBACK_MODE` enum (`..._INCLUDE_TARGET_PROCESS_TREE` / `..._EXCLUDE_TARGET_PROCESS_TREE`):
  https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode
- `ActivateAudioInterfaceAsync` function signature, requires **Windows 8+** for the
  function itself, the process-loopback activation type requires Build 20348+;
  device path `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK` activates process-loopback:
  https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync
- Microsoft's official reference implementation, "Application Loopback API Capture
  Sample" (C++, `ApplicationLoopbackAudio` in Windows-classic-samples) — the
  canonical worked example Microsoft points to from all three struct/enum pages
  above: https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/
- NAudio does **not** support process-specific loopback out of the box. Confirmed
  via NAudio issue #878 ("Capture specific process with WasapiLoopbackCapture",
  opened 2022-02-17): NAudio's `WasapiLoopbackCapture` only wraps the classic
  full-endpoint loopback (`AUDCLNT_STREAMFLAGS_LOOPBACK` on the render endpoint),
  not `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`. The issue explicitly points
  back at the Microsoft sample as the way to do it, i.e. raw interop:
  https://github.com/naudio/NAudio/issues/878
- However, NAudio's own `WasapiOutRT.cs` (used for its WinRT/UWP output path)
  already contains a working, minimal C# `DllImport` + COM-interface declaration
  for `ActivateAudioInterfaceAsync` / `IActivateAudioInterfaceCompletionHandler` /
  `IActivateAudioInterfaceAsyncOperation` — this is the interop pattern this
  project's `Interop/` layer is modeled on (adapted for process-loopback params
  instead of a WinRT device id):
  https://github.com/naudio/NAudio/blob/30aa44ede137aa893358d9ca98dfe60096c21a48/NAudio/Wave/WaveOutputs/WasapiOutRT.cs

**Decision: hand-rolled COM interop for the loopback-capture path, NAudio for
everything else.** NAudio (>= 2.2, `NAudio.Wasapi` + `NAudio.CoreAudioApi`) is used
for: normal microphone capture (`WasapiCapture`), device enumeration
(`MMDeviceEnumerator`), and audio-session enumeration (`AudioSessionManager2` /
`AudioSessionControl.GetProcessID`, confirmed present in `NAudio.Wasapi/CoreAudioApi/AudioSessionControl.cs`
— `GetProcessID` reads `IAudioSessionControl2.GetProcessId`, `State` reads session
Active/Inactive/Expired). Only the process-loopback activation itself is raw P/Invoke,
isolated behind `IProcessLoopbackCapture` so the rest of the app never touches COM directly.

---

## 2. Project structure

```
callnotes-windows/
├── LICENSE                          PolyForm Noncommercial 1.0.0, Copyright Michael Czesun
├── README.md                        English readme (same value-prop framing as Mac README.md)
├── README.de.md                     German readme
├── docs/
│   └── contract.md                  This file
├── CallTap.sln                      Solution: CallTap.Core, CallTap.Cli, CallTap.Tray, CallTap.Tests
│
├── src/
│   ├── CallTap.Core/                Class library: no UI, no Main — reusable by CLI + Tray
│   │   ├── CallTap.Core.csproj      net8.0-windows, no UseWPF
│   │   ├── Interop/
│   │   │   ├── NativeMethods.cs         DllImport: ActivateAudioInterfaceAsync (Mmdevapi.dll),
│   │   │   │                            CoTaskMemAlloc/Free, PROPVARIANT helpers
│   │   │   ├── ComInterfaces.cs         IActivateAudioInterfaceCompletionHandler,
│   │   │   │                            IActivateAudioInterfaceAsyncOperation, IAgileObject,
│   │   │   │                            IAudioClient minimal subset used post-activation
│   │   │   ├── Structs.cs               AUDIOCLIENT_ACTIVATION_PARAMS,
│   │   │   │                            AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS,
│   │   │   │                            PROCESS_LOOPBACK_MODE, WAVEFORMATEX, PROPVARIANT
│   │   │   └── Guids.cs                 IID_IAudioClient, IID_IAudioClient2,
│   │   │                                CLSID/IID constants used across the interop layer
│   │   ├── Capture/
│   │   │   ├── ProcessLoopbackCapture.cs   IProcessLoopbackCapture impl — activates +
│   │   │   │                               initializes IAudioClient for one target PID,
│   │   │   │                               runs the capture thread, raises DataAvailable
│   │   │   ├── MicrophoneCapture.cs        Wraps NAudio WasapiCapture (shared default
│   │   │   │                               capture endpoint), same DataAvailable shape
│   │   │   ├── IAudioTrackCapture.cs        Shared interface both capturers implement
│   │   │   │                               (Start, Stop, DataAvailable event, level metering)
│   │   │   └── AudioSessionWatcher.cs      NAudio AudioSessionManager2-based poller:
│   │   │                                   "which process currently has the mic active"
│   │   │                                   (mirrors macOS procIsRunningInput / calltap procs)
│   │   ├── Recording/
│   │   │   ├── RecordingSession.cs         Owns mic + system capture pair, writes
│   │   │   │                               mic.wav/system.wav + meta.json + levels.json
│   │   │   │                               (mirrors macOS RecordingSession)
│   │   │   └── WavFileWriter.cs            Streaming WAV writer (NAudio WaveFileWriter
│   │   │                                   wrapper), PCM16 or Float32 per capture format
│   │   ├── Watch/
│   │   │   ├── WatchConfig.cs               Deserializes config.json (System.Text.Json),
│   │   │   │                               same keys/semantics as macOS WatchConfig
│   │   │   ├── ProcessMatcher.cs            Executable-name allowlist matcher (globs,
│   │   │   │                               "*" wildcard) — Windows analogue of
│   │   │   │                               WatchConfig.matches(bundle:) but on exe name
│   │   │   │                               + parent/child process family
│   │   │   └── CallWatcher.cs               Poll loop: detect active mic session among
│   │   │                                   allowlisted processes, minSeconds/stopGraceSeconds/
│   │   │                                   maxHours state machine, suppressBundle logic,
│   │   │                                   abort-file polling, state/current-call.json +
│   │   │                                   state/pending/*.json + levels.json writers
│   │   │                                   (line-for-line port of calltap.swift cmdWatch)
│   │   └── Config/
│   │       └── Paths.cs                     Central %APPDATA%/%USERPROFILE% path resolution
│   │
│   ├── CallTap.Cli/                  Console app = the Windows "calltap" binary
│   │   ├── CallTap.Cli.csproj        net8.0-windows, OutputType Exe, AssemblyName calltap,
│   │   │                             optional <PublishSingleFile>true</PublishSingleFile>
│   │   │                             + <SelfContained>true</SelfContained> for release builds
│   │   └── Program.cs                Subcommand dispatch: procs / setup / record / watch
│   │                                 (mirrors calltap.swift usage() + switch cmd)
│   │
│   ├── CallTap.Tray/                 WPF tray app = the Windows "CallNotes" menu-bar equivalent
│   │   ├── CallTap.Tray.csproj       net8.0-windows, <UseWPF>true</UseWPF>,
│   │   │                             <ApplicationIcon>Assets/tray.ico</ApplicationIcon>
│   │   ├── App.xaml / App.xaml.cs    NotifyIcon host (Hardcodet.NotifyIcon.Wpf or
│   │   │                             System.Windows.Forms.NotifyIcon interop), starts/stops
│   │   │                             the CallTap.Core watch loop in-process (no separate
│   │   │                             daemon process — simpler on Windows than launchd)
│   │   ├── Views/
│   │   │   ├── LiveCallView.xaml(.cs)      Live level meters (mic/system), call timer,
│   │   │   │                               participant-name entry popup
│   │   │   ├── ProcessingView.xaml(.cs)    Phase indicator (reads state/processing.json)
│   │   │   ├── SpeakerAssignView.xaml(.cs) Reads state/pending/*.json, plays clip per
│   │   │   │                               speaker, dropdown to assign name
│   │   │   │                               (Windows analogue of apply-speakers.sh)
│   │   │   └── SettingsView.xaml(.cs)      Full settings UI incl. ⓘ explainers, mirrors
│   │   │                                   macOS SettingsApp.swift sections
│   │   ├── Services/
│   │   │   ├── TrayIconController.cs       Icon state (idle/recording/processing)
│   │   │   ├── AudioPlaybackService.cs     Plays speaker-snippet clips (NAudio WasapiOut)
│   │   │   └── FirstRunWizard.cs           Onboarding: pick apps, model paths, permissions
│   │   └── Assets/
│   │       └── tray.ico, tray-recording.ico, tray-processing.ico
│   │
│   └── CallTap.Tests/                 Unit + integration tests (xUnit)
│       ├── CallTap.Tests.csproj       net8.0 (NOT net8.0-windows where avoidable, but
│       │                             COM-dependent tests need net8.0-windows + [WindowsOnlyFact])
│       ├── ProcessMatcherTests.cs
│       ├── WatchConfigTests.cs
│       ├── StateFileRoundTripTests.cs
│       └── ProcessLoopbackCaptureSmokeTests.cs   Runs ONLY on windows-latest CI runner
│                                                  (self-hosted has no audio devices either —
│                                                  see section 8, "no local Windows box")
│
├── pipeline/                          Python processing pipeline — ported from Mac
│   │                                  process-call.sh, same stages, same stdlib-first style
│   ├── process_call.py                Orchestrator: entry point, equivalent to process-call.sh
│   │                                  Called by CallTap.Tray or manually:
│   │                                  `python pipeline/process_call.py <rec-dir>`
│   ├── config.py                      Loads %APPDATA%/callnotes/config.json (same keys)
│   ├── transcribe.py                  whisper.cpp (whisper-cli.exe) or Groq cloud fallback —
│   │                                  same two-track transcribe() logic as bash version
│   ├── diarize.py                     sherpa-onnx speaker diarization — reused near-verbatim
│   │                                  from macOS diarize.py (pure Python, no macOS API calls)
│   ├── merge_transcript.py            Port of merge-transcript.py (dialog merge, speaker
│   │                                  numbering) — same JSON contract in/out
│   ├── summarize.py                   Claude Code CLI / OpenAI-compatible / off — same
│   │                                  prompt templates (DE/EN) as process-call.sh
│   ├── note_writer.py                 Builds the Markdown note + MOC maintenance
│   │                                  (anrufe-moc.md / calls-moc.md), identical frontmatter
│   ├── destinations.py                Nextcloud (WebDAV), Notion API delivery
│   │                                  (Apple Notes destination: N/A on Windows, see section 9)
│   └── requirements.txt               Pinned deps (must match Mac pipeline versions where
│                                       the same library is used, e.g. same sherpa-onnx)
│
├── installer/
│   ├── install.ps1                    PowerShell installer: winget/choco deps (ffmpeg,
│   │                                  python), venv setup, writes default config.json,
│   │                                  registers Scheduled Task or Startup shortcut for
│   │                                  CallTap.Tray, requests TCC-equivalent permissions
│   │                                  (Settings > Privacy > Microphone) via a guided prompt
│   ├── uninstall.ps1
│   └── CallTap.Tray.appxmanifest      Optional: only if packaged via MSIX later (v1 ships
│                                      as a plain signed EXE + PowerShell installer, no store)
│
├── config.example.json                Same shape as macOS config.example.json, but "apps"
│                                       holds process executable names, not bundle IDs
│                                       (see section 4)
│
├── .github/
│   └── workflows/
│       └── ci.yml                     windows-latest matrix: build (net8.0-windows), test
│                                      (xUnit, WindowsOnlyFact-gated), optionally
│                                      `dotnet publish -r win-x64 --self-contained` artifact
│                                      upload. THIS is the only place the app is actually
│                                      compiled/run against a real Windows audio stack —
│                                      there is no local Windows machine for this project
│                                      (see section 8).
│
└── .gitignore                         bin/, obj/, *.user, venv/, __pycache__/
```

### Design notes on the split

- **`CallTap.Core` has zero WPF/console dependencies.** Both `CallTap.Cli` (headless,
  scriptable, CI-testable) and `CallTap.Tray` (interactive) reference it. This mirrors
  how `calltap.swift` (binary) and the (separate, Mac-only) menu-bar app both operate
  on the same `~/CallNotes/state/*` files without sharing a process — on Windows we
  additionally allow `CallTap.Tray` to run the watch loop **in-process** (as a
  background `Task`/thread) since Windows has no `launchd`; `CallTap.Cli watch` remains
  available for headless/service use and for CI smoke tests.
- **The Python pipeline is a separate, language-stable layer**, exactly like on macOS.
  This is deliberate: diarization (sherpa-onnx) and whisper.cpp invocation are already
  solved problems in Python on the Mac side, and reimplementing them in C# would both
  duplicate work and risk behavioral drift between the two sibling projects. The C#
  recorder's only job is producing `mic.wav` + `system.wav` + `meta.json` in the exact
  shape the pipeline already expects.

---

## 3. Shared file formats — Windows paths and layout

All state lives under two roots, both configurable, mirroring the Mac's
`~/.config/callnotes/` (config) vs `~/CallNotes/` (data) split:

| Purpose | macOS | Windows |
|---|---|---|
| Config file | `~/.config/callnotes/config.json` | `%APPDATA%\callnotes\config.json` (i.e. `C:\Users\<user>\AppData\Roaming\callnotes\config.json`) |
| Secrets (API keys, optional) | `~/.config/callnotes/secrets.env` | `%APPDATA%\callnotes\secrets.env` |
| Data root (`outDir` default) | `~/CallNotes` | `%USERPROFILE%\CallNotes` (i.e. `C:\Users\<user>\CallNotes`) |
| Notes | `~/CallNotes/notes` | `%USERPROFILE%\CallNotes\notes` |
| Audio archive | `~/CallNotes/audio` | `%USERPROFILE%\CallNotes\audio` |
| Raw in-progress recordings | `~/CallNotes/rec/<stamp>_<app>/` | `%USERPROFILE%\CallNotes\rec\<stamp>_<app>\` |
| Failed recordings | `~/CallNotes/failed/` | `%USERPROFILE%\CallNotes\failed\` |
| Review clips (speaker snippets) | `~/CallNotes/review/<stamp>/` | `%USERPROFILE%\CallNotes\review\<stamp>\` |
| State dir | `~/CallNotes/state/` | `%USERPROFILE%\CallNotes\state\` |
| Current call marker | `~/CallNotes/state/current-call.json` | `%USERPROFILE%\CallNotes\state\current-call.json` |
| Processing phase marker | `~/CallNotes/state/processing.json` | `%USERPROFILE%\CallNotes\state\processing.json` |
| Speaker-assignment queue | `~/CallNotes/state/pending/<stamp>.json` | `%USERPROFILE%\CallNotes\state\pending\<stamp>.json` |
| Per-recording live levels | `<rec-dir>/levels.json` | `<rec-dir>\levels.json` |
| Logs | `~/CallNotes/log/*.log` | `%USERPROFILE%\CallNotes\log\*.log` |

`Paths.cs` resolves these via `Environment.GetFolderPath(SpecialFolder.ApplicationData)`
for the config root and `Environment.GetFolderPath(SpecialFolder.UserProfile)` +
`"CallNotes"` for the data root, both overridable exactly like the Mac's `outDir` /
`notesDir` / `audioDir` config keys and the `CALLNOTES_CONFIG` env var
(Windows: `CALLNOTES_CONFIG` env var too, same name, for CI + power users).

### 3.1 `config.json` — key-for-key parity with the Mac

Every key below exists on macOS today (see `config.example.json`); semantics are
identical unless noted. New Windows-only keys are marked **(new)**.

```jsonc
{
  "_hinweis": "Nach %APPDATA%/callnotes/config.json kopieren (macht install.ps1 automatisch).",

  // Was auf macOS Bundle-IDs sind, sind auf Windows Executable-Namen (siehe 4.1).
  "apps": [
    "WhatsApp.exe",
    "Zoom.exe",
    "ms-teams.exe",
    "Discord.exe"
  ],

  "minSeconds": 20,
  "stopGraceSeconds": 6,
  "maxHours": 4,

  // macOS: "app" | "global" (Process-Tap-Scope). Windows-Aequivalent: gleiche
  // zwei Werte, aber "app" bedeutet hier "dieser Prozess + Kindprozessbaum via
  // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE" statt Core-Audio-Prozessfamilie.
  "tapScope": "app",

  "outDir": "%USERPROFILE%\\CallNotes",
  "notesDir": "%USERPROFILE%\\CallNotes\\notes",
  "audioDir": "%USERPROFILE%\\CallNotes\\audio",
  "mirrorDir": "",

  "whisperModel": "%USERPROFILE%\\models\\ggml-large-v3-turbo-q5_0.bin",
  "language": "de",
  "diarize": true,
  "diarizeThreshold": 0.6,

  "transcriber": "local",
  "groqApiKey": "",

  "summarizer": "claude",
  "summarizerUrl": "",
  "summarizerModel": "",
  "summarizerApiKey": "",
  "claudeBin": "",

  "speakerSelf": "Ich",
  "speakerPeer": "Gesprächspartner",
  "context": "",

  "noteSections": ["kurzfassung", "besprochen", "todos"],

  "destinations": {
    "appleNotes": false,
    "nextcloud": false,
    "notion": false
  },
  "nextcloudUrl": "",
  "nextcloudUser": "",
  "nextcloudAppPass": "",
  "notionToken": "",
  "notionParent": "",

  "notesMoc": true,
  "ntfyUrl": "",
  "postScript": "WIRD_VON_INSTALL_PS1_GESETZT",
  "uiLanguage": "system",

  "venvPython": "%LOCALAPPDATA%\\callnotes\\venv\\Scripts\\python.exe",

  "micDeviceId": "",            // (new) NAudio MMDevice.ID override; "" = default capture endpoint
  "processLoopbackMode": "includeTree"  // (new) "includeTree" | "excludeTree" — maps 1:1 to
                                          // PROCESS_LOOPBACK_MODE; exposed for advanced users/debug,
                                          // default is always includeTree in the UI
}
```

Notes:
- `%APPDATA%`, `%USERPROFILE%`, `%LOCALAPPDATA%` in the JSON file are expanded by
  `Paths.cs`/`WatchConfig.cs` at load time (same idea as the Mac's `expandingTildeInPath`
  / Python `os.path.expanduser`); they are **not** resolved by the OS since this is a
  plain JSON string, not an actual env-var reference passed to a shell.
- `apps`, `minSeconds`, `stopGraceSeconds`, `maxHours`, `outDir`, `postScript`,
  `tapScope` are consumed by `CallTap.Core` (C#, watch loop). Everything else is
  consumed by `pipeline/config.py` (Python, post-processing) — identical split of
  responsibility to the Mac (`calltap.swift` WatchConfig vs `process-call.sh`'s
  `eval "$(python3 ...)"` block).
- `venvPython` replaces the Mac's `~/.local/share/callnotes/venv/bin/python3` default
  with a Windows venv `Scripts\python.exe` path; same key name, same purpose.

### 3.2 `state/current-call.json`

Identical shape to macOS `writeCurrentCall()`:

```json
{
  "dir": "C:\\Users\\michael\\CallNotes\\rec\\2026-07-03_161530_whatsapp",
  "app": "WhatsApp.exe",
  "appName": "whatsapp",
  "start": "2026-07-03T16:15:30Z"
}
```

`app` holds the Windows executable name (e.g. `WhatsApp.exe`) in the field that on
macOS holds the bundle ID (e.g. `net.whatsapp.WhatsApp`) — same field name, same
consumer contract (Tray UI reads `app`/`appName`/`start`/`dir`), different content
convention per platform (documented in section 4).

### 3.3 `state/processing.json`

Identical to macOS `phase()`:

```json
{"stamp": "2026-07-03_161530", "phase": "Transkription läuft…"}
```

Same phase strings per language as `process-call.sh` (`T_PH_TRANS`, `T_PH_DIA`,
`T_PH_AI`, `T_PH_STORE`), produced by `pipeline/process_call.py`.

### 3.4 `state/pending/<stamp>.json`

Identical to macOS's speaker-assignment payload written at the end of
`process-call.sh` (the Python block producing `speaker_N.m4a` clips + suggestions):

```json
{
  "stamp": "2026-07-03_161530",
  "app": "ms-teams.exe",
  "note": "C:\\Users\\michael\\CallNotes\\notes\\2026-07-03-1615-anruf-kickoff.md",
  "speakers": [
    {"label": "Sprecher 1", "clip": "C:\\...\\review\\2026-07-03_161530\\speaker_1.m4a",
     "suggestion": "Stefan", "totalSec": 42.3},
    {"label": "Sprecher 2", "clip": "C:\\...\\review\\2026-07-03_161530\\speaker_2.m4a",
     "suggestion": "", "totalSec": 11.0}
  ],
  "participants": ["Stefan", "Anna"]
}
```

Windows pipeline writes `.m4a` via the same ffmpeg invocation as macOS (ffmpeg is a
cross-platform dependency either way, installed via the PowerShell installer).

### 3.5 `<rec-dir>/levels.json`

Identical to macOS `startLevels()` output, written every ~0.35s during a call:

```json
{"mic": 0.42, "sys": 0.18, "t": 1751558130.512}
```

### 3.6 `<rec-dir>/meta.json`

Identical to macOS `stopAndFinalize()` meta, with `sysFrames`/`micFrames` now
frame counts from the WASAPI capture buffers instead of Core Audio taps:

```json
{
  "app": "Zoom.exe",
  "appName": "zoom",
  "start": "2026-07-03T16:15:30Z",
  "end": "2026-07-03T16:47:02Z",
  "durationSec": 1892,
  "sysFrames": 90816000,
  "micFrames": 90816000
}
```

### 3.7 Audio track files: `.wav` instead of `.caf`

macOS writes `mic.caf` / `system.caf` (Core Audio Format, float32, native sample
rate from `AVAudioEngine`/tap format). Windows has no CAF ecosystem; the direct,
lossless, ffmpeg/whisper.cpp-friendly equivalent is **PCM WAV**:

- `mic.wav` — from `MicrophoneCapture` (NAudio `WasapiCapture`, default mic endpoint
  mix format, typically 48000 Hz / 32-bit float or 16-bit PCM depending on the
  driver's shared-mode mix format — captured as-is, no resampling at capture time,
  exactly like the Mac captures at the tap's native format).
- `system.wav` — from `ProcessLoopbackCapture`, written in whatever `WAVEFORMATEX`
  the activated `IAudioClient::GetMixFormat()` (or the format forced via
  `IAudioClient2::SetClientProperties` + `Initialize`) reports — see section 6.4.

`pipeline/transcribe.py`'s `ffmpeg -i <in> -ar 16000 -ac 1 -c:a pcm_s16le <out>`
downsampling step is unchanged from the bash version — it already normalizes
whatever the input format is, so the mic/system WAV's native sample rate does not
need to match whisper's 16kHz requirement at capture time.

---

## 4. `apps` allowlist: process names instead of bundle IDs

### 4.1 Convention

macOS uses reverse-DNS bundle identifiers (`net.whatsapp.WhatsApp`,
`us.zoom.xos`, `com.microsoft.teams2`, `com.hnc.Discord`) because that's the unit
Core Audio's `kAudioProcessPropertyBundleID` exposes. Windows processes have no
bundle-ID equivalent; the natural unit is the **executable file name**
(`Process.ProcessName` + `.exe`, matched case-insensitively), which is what
Task Manager / `Get-Process` / Audio Session `DisplayName` surface to users too.

v1 target-app executable names (verified against each vendor's actual installed
binary name, not guessed):

| App | Windows exe |
|---|---|
| WhatsApp Desktop (Microsoft Store / native Windows app, Electron-free UWP-ish shell) | `WhatsApp.exe` |
| Zoom | `Zoom.exe` |
| Microsoft Teams (new Teams, 2023+ WebView2-based client) | `ms-teams.exe` |
| Discord | `Discord.exe` |

Same `apps` array shape as macOS, same wildcard suffix rule
(`"Foo*"` matches prefix, `"*"` matches everything) reused verbatim from
`WatchConfig.matches(_:)`.

### 4.2 Process-family resolution (Electron/WebView2 helper processes)

macOS's `tapTargets(trigger:bundle:)` walks all Core Audio process objects and
collects every process whose bundle ID equals or is a dotted child of the
triggering app's bundle ID (`b == bundle || b.hasPrefix(bundle + ".")`) — this
matters because Electron/Chromium apps often play audio from a renderer/GPU
helper process, not the main executable.

Windows equivalent, since there is no bundle-ID hierarchy: **process tree by
parent PID**, using `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE` on the
*main window process* of the matched executable. This is actually **simpler and
more robust than the macOS bundle-prefix heuristic** because the include-tree flag
is a first-class capability of the WASAPI Process Loopback API itself — the OS
walks the child-process tree for us; `ProcessMatcher`/`CallWatcher` only need to
find the correct **root PID** (the top-level process matching the allowlisted exe
name, found via `Process.GetProcessesByName`), not enumerate helpers manually.
`tapScope: "global"` still exists as an escape hatch (falls back to classic
full-endpoint loopback via `AUDCLNT_STREAMFLAGS_LOOPBACK`, no process filter,
same meaning as macOS's `CATapDescription(stereoGlobalTapButExcludeProcesses:)`).

### 4.3 "Which process has the microphone" — Windows Audio Session enumeration

macOS's `procIsRunningInput`/`processObjects()` (Core Audio `kAudioHardwarePropertyProcessObjectList`
+ `kAudioProcessPropertyIsRunningInput`) has a direct Windows analogue:
**`IAudioSessionManager2::GetSessionEnumerator`** on the **default capture endpoint**
(the microphone), enumerating `IAudioSessionControl2` sessions and reading
`GetProcessId` + `GetState()` (`AudioSessionStateActive` when the process is
actively capturing).

This is fully covered by **NAudio.CoreAudioApi**, no raw interop needed — confirmed
present in `NAudio.Wasapi/CoreAudioApi/AudioSessionControl.cs`:
`AudioSessionControl.GetProcessID` (reads `IAudioSessionControl2.GetProcessId`)
and `AudioSessionControl.State` (Active/Inactive/Expired).
https://github.com/naudio/NAudio/blob/master/NAudio.Wasapi/CoreAudioApi/AudioSessionControl.cs

`AudioSessionWatcher.cs` implementation sketch (this is the direct analogue of
`calltap procs` / `cmdProcs`):

```csharp
using NAudio.CoreAudioApi;

namespace CallTap.Core.Capture;

public sealed record ActiveMicSession(int ProcessId, string ProcessName, string DisplayName);

public static class AudioSessionWatcher
{
    /// Mirrors macOS `processObjects()` + `procIsRunningInput` + `procPID`:
    /// returns every process currently holding an ACTIVE session on the
    /// default microphone (capture) endpoint.
    public static IReadOnlyList<ActiveMicSession> GetActiveMicSessions()
    {
        var result = new List<ActiveMicSession>();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.State != AudioSessionState.AudioSessionStateActive) continue;
            uint pid = session.GetProcessID;
            string name = "";
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { /* process exited between enumeration and lookup */ }
            result.Add(new ActiveMicSession((int)pid, name, session.DisplayName));
        }
        return result;
    }
}
```

Notes on fidelity to the source API (so this compiles against real NAudio, not a
guess): `MMDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role role)`,
`MMDevice.AudioSessionManager.Sessions` (a `SessionCollection`, indexer returns
`AudioSessionControl`), `AudioSessionControl.State` (`AudioSessionState` enum),
`AudioSessionControl.GetProcessID` (uint) — all confirmed via the NAudio source
tree referenced above. `Role.Communications` mirrors "the device an actual VoIP
call would render/capture through" (closest Windows concept to macOS's default
input device used for the mic tap); `Role.Console` (`Role.Multimedia`) can be
polled additionally if a given app's session shows up there instead — surfaced as
a fallback check in `CallWatcher`, not a single hardcoded Role.

---

## 5. CLI contract — `calltap`

Same four subcommands as macOS `calltap.swift`, same flag names, same default
config path convention (adapted to Windows), same output framing so a user
switching between the two READMEs sees the same mental model.

```
calltap.exe procs [--watch]
    Lists processes with an active microphone session (AudioSessionWatcher).
    --watch: clears screen, refreshes every 1.5s, Ctrl+C to stop.
    Exit 0 always (informational command).

calltap.exe setup
    One-time permission/capability probe:
      1) Requests microphone access (triggers the Windows privacy consent
         prompt the first time an app requests raw mic capture — Settings >
         Privacy & security > Microphone must allow desktop apps).
      2) Runs a throwaway ~0.5s process-loopback activation against calltap's
         own PID (harmless self-test, mirrors macOS's short Process Tap
         self-test in cmdWatch) to confirm ActivateAudioInterfaceAsync +
         AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK works on this machine/OS
         build (fails clearly, with the Build-20348 remark, on older Windows 10).
    Exit 0 if both succeed, 1 otherwise, printing the same kind of "Settings >
    ..." remediation string as the macOS version.

calltap.exe record --out DIR [--seconds N] [--exe NAME]
    Manual recording, Ctrl+C stops (SIGINT via Console.CancelKeyPress, same
    idea as macOS's signal(SIGINT/SIGTERM, ...) handlers).
    --exe NAME: restrict system-audio capture to the process tree rooted at
    the first running process matching NAME (case-insensitive, ".exe" optional)
    — Windows analogue of macOS `--bundle ID`. Omitted: --exe unset behaves like
    macOS's bundle-less manual recording, i.e. "manual"/"manuell" app label,
    global-scope system audio (classic loopback, no process filter).
    Writes mic.wav + system.wav + meta.json into DIR, same as macOS's mic.caf/
    system.caf/meta.json.

calltap.exe watch [--config FILE]
    Daemon/foreground loop: default config path
    %APPDATA%\callnotes\config.json (override: --config or CALLNOTES_CONFIG
    env var). Same detection/state-machine semantics as macOS cmdWatch (see
    section 6). Runs until Ctrl+C (console) or process termination (Scheduled
    Task / Tray-hosted).
```

`--debug` is accepted by `record` and `watch` exactly like macOS, enabling verbose
buffer/format logging from the interop layer (chosen ABL-buffer equivalent: which
`IAudioCaptureClient::GetBuffer` call returned data, frame counts, detected mix
format).

---

## 6. `calltap watch` — behavior contract (ported from `calltap.swift` `cmdWatch`)

This section is a literal behavioral port of the Mac's watch daemon logic, since
that logic (not the audio capture API) is the actual product behavior users rely
on. Every rule below has a 1:1 line in `calltap.swift` referenced for traceability.

1. **Startup cleanup** (`cmdWatch` lines ~578–591): remove any stale
   `state/current-call.json` from a crashed previous run; move any `rec/*`
   directory lacking a `meta.json` into `failed/` (orphaned recording from a
   crash) and delete its stray `levels.json`.
2. **Self-test on startup**: harmless mic-permission probe + a throwaway
   process-loopback activate/release cycle (not a full capture), logging a clear
   warning (not a crash) if either fails — same "warn, don't crash" posture as
   macOS's Core Audio self-test + `MicRecorder.ensurePermission()` check.
3. **Poll loop, every 2s** (`timer.schedule(deadline: .now() + 1, repeating: 2.0)`):
   - If an `abort` marker file exists in the active recording's directory, discard
     immediately (see rule 7).
   - Enumerate active-mic sessions (`AudioSessionWatcher.GetActiveMicSessions()`,
     the Windows analogue of `processObjects()` + `procIsRunningInput`).
   - Skip calltap's own process by PID (Windows has no bundle-ID self-collision
     risk since we already have the exact PID, simpler than macOS's
     bundle-empty-string-plus-process-name fallback check).
   - First allowlist-matching active session (`ProcessMatcher`, same semantics as
     `WatchConfig.matches(_:)`) becomes the `active` candidate for this tick.
   - Any active-but-unmatched session logs a one-time "not in your `apps` list"
     info line per process name (`loggedUnknown` set, same dedup behavior as macOS).
4. **Suppress-after-discard state** (`suppressBundle`/`suppressIdleSince`, lines
   ~716–730): after a user discards a call from the Tray "don't record this" popup
   (rule 7), the same exe name is *not* re-triggered while it's still the active
   session; only once it's been idle (no active mic session matching it) for
   `stopGraceSeconds` does the suppression clear and the app becomes recordable
   again for its *next* call. Exact same state machine as macOS, same variable
   names carried into `CallWatcher` (`_suppressExe`, `_suppressIdleSince`).
5. **Start recording** when `active` is set and no session is running yet:
   - Debounce start-errors: if the last start attempt errored less than 60s ago,
     skip this tick (no prompt/error storm) — same `lastStartError` 60s guard.
   - Directory name: `rec\<yyyy-MM-dd_HHmmss>_<shortname>\` (second-precision
     timestamp, same collision-avoidance rationale as macOS `dirStamp`).
   - `shortName` derivation: exe name without `.exe`, lowercased (Windows
     analogue of macOS's bundle-last-component-lowercased / process-name
     fallback).
   - Resolve the root PID for the matched exe (see 4.2), start
     `ProcessLoopbackCapture` (include-tree mode, or global/classic loopback if
     `tapScope: "global"`) + `MicrophoneCapture` in parallel; on system-capture
     failure, do not start the mic track either (mirrors macOS: `sys.start` failure
     must not leave an orphaned mic-only session) and vice versa.
   - On success: write `state/current-call.json`, log
     `REC START <exe> (tap: <n> process(es)/global) -> <dir>`.
6. **Maximum duration**: if a session has run longer than `maxHours` (default 4),
   force-finish with reason "Maximaldauer erreicht" / "Max duration reached" —
   identical to macOS.
7. **Stop conditions**:
   - **Natural end**: no allowlisted session active anymore for
     `stopGraceSeconds` (default 6) consecutive seconds ⇒ `finish(reason:
     "Anruf beendet")`. If total duration `< minSeconds` (default 20), the
     recording is **discarded silently** (directory deleted, nothing queued for
     processing) — exact same "too short, discarded" rule as macOS. Otherwise the
     recording directory is handed to `pipeline/process_call.py` (spawned
     detached, same as macOS's `spawnPost` using `nohup bash ... &`; Windows uses
     `Process.Start` with `UseShellExecute = false`, redirected output appended to
     `log\process.log`, and **no console window** via
     `CreateNoWindow = true`/`WindowStyle = Hidden`).
   - **User-initiated discard** (Tray "Don't record this" button, mirrors macOS's
     `abort` marker file protocol): Tray writes an `abort` file into the active
     recording's directory; next poll tick sees it, stops both capture threads,
     deletes the recording directory entirely, engages the suppress state (rule 4),
     removes `state/current-call.json`. **Nothing is queued for processing.**
8. **Live status files** written continuously while recording (see 3.5/3.6),
   removed on stop; **`state/current-call.json`** written on start, removed on stop
   or discard, so the Tray always has a single source of truth for "is a call
   being recorded right now, and which app."
9. **Ctrl+C / process exit**: if a session is active, finish it with reason
   "callwatch gestoppt"/"call watcher stopped" before exiting — same graceful-stop
   contract as macOS's `SIGINT`/`SIGTERM` handlers.

---

## 7. C# interop — compilable-shape reference

This is the actual interop layer contract: exact usings, DllImport signatures,
GUIDs, and marshaling attributes. Written to compile against .NET 8 /
`net8.0-windows` with `AllowUnsafeBlocks` enabled. GUIDs for
`IActivateAudioInterfaceCompletionHandler` / `IActivateAudioInterfaceAsyncOperation`
are the ones already shipped and used in production inside NAudio's own
`WasapiOutRT.cs` (see section 1 sources) — reusing known-good, already-in-the-wild
GUIDs rather than re-deriving them from the (GUID-omitting) Microsoft Learn pages.

### 7.1 `Interop/Structs.cs`

```csharp
using System.Runtime.InteropServices;

namespace CallTap.Core.Interop;

/// <summary>
/// AUDIOCLIENT_ACTIVATION_TYPE — audioclientactivationparams.h.
/// https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-audioclient_activation_type
/// </summary>
internal enum AUDIOCLIENT_ACTIVATION_TYPE
{
    AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0,
    AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1,
}

/// <summary>
/// PROCESS_LOOPBACK_MODE — audioclientactivationparams.h. Min supported client:
/// Windows 10 Build 20348.
/// https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ne-audioclientactivationparams-process_loopback_mode
/// </summary>
internal enum PROCESS_LOOPBACK_MODE
{
    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0,
    PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1,
}

/// <summary>
/// AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS — audioclientactivationparams.h.
/// https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
{
    public uint TargetProcessId;
    public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
}

/// <summary>
/// AUDIOCLIENT_ACTIVATION_PARAMS — a tagged union in C++ (ActivationType +
/// an anonymous union currently holding only ProcessLoopbackParams). C# has no
/// native union; [FieldOffset] on both fields models the same memory layout.
/// https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_activation_params
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct AUDIOCLIENT_ACTIVATION_PARAMS
{
    [FieldOffset(0)] public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
    // Explicit union member: offset 4 assumes the enum marshals as a 4-byte DWORD,
    // which matches audioclientactivationparams.h backing type (int-based enum).
    [FieldOffset(4)] public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
}

/// <summary>
/// Minimal PROPVARIANT shape sufficient for the VT_BLOB case
/// ActivateAudioInterfaceAsync's activationParams argument requires here.
/// Full PROPVARIANT is a much larger tagged union; only vt/blob are used.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BLOB
{
    public uint cbSize;
    public IntPtr pBlobData;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PROPVARIANT_BLOB
{
    [FieldOffset(0)] public ushort vt;     // VT_BLOB = 65
    [FieldOffset(8)] public BLOB blob;     // offset 8: matches PROPVARIANT's
                                            // 8-byte header (vt + wReserved1-3)
                                            // before the union payload on x64
}

internal const ushort VT_BLOB = 65;
```

### 7.2 `Interop/ComInterfaces.cs`

GUIDs for the two completion-handler interfaces below are the values already
compiled and shipped inside NAudio's `WasapiOutRT.cs` (confirmed via source,
section 1) — used here verbatim rather than re-typed from a non-GUID-bearing
docs page, to avoid a transcription error in a value that must match byte-for-byte.

```csharp
using System.Runtime.InteropServices;

namespace CallTap.Core.Interop;

[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(
        out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object activateInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>
/// Minimal IAudioClient subset actually used by ProcessLoopbackCapture.
/// IID from audioclient.h (well-known, unchanged since Vista WASAPI).
/// </summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    void Initialize(
        AUDCLNT_SHAREMODE shareMode,
        AUDCLNT_STREAMFLAGS streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        [In] ref WAVEFORMATEX pFormat,
        [In] IntPtr audioSessionGuid);

    void GetBufferSize(out uint numBufferFrames);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out uint numPaddingFrames);
    void IsFormatSupported(AUDCLNT_SHAREMODE shareMode, [In] ref WAVEFORMATEX format, out IntPtr closestMatch);
    void GetMixFormat(out IntPtr deviceFormatPtr);
    void GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr eventHandle);
    void GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

internal enum AUDCLNT_SHAREMODE { Shared = 0, Exclusive = 1 }

[Flags]
internal enum AUDCLNT_STREAMFLAGS : uint
{
    None = 0,
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000,
    AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000, // only relevant for the classic
                                                // full-endpoint fallback path,
                                                // NOT used for process loopback
                                                // (process loopback activation
                                                // implies loopback semantics via
                                                // the activation type itself)
}

[StructLayout(LayoutKind.Sequential)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

/// <summary>IAudioCaptureClient — audioclient.h, well-known IID.</summary>
[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead,
        out uint bufferFlags, out ulong devicePosition, out ulong qpcPosition);
    void ReleaseBuffer(uint numFramesRead);
    void GetNextPacketSize(out uint numFramesInNextPacket);
}
```

### 7.3 `Interop/NativeMethods.cs`

`ActivateAudioInterfaceAsync` DllImport signature below matches the one already
compiled and used in production by NAudio's `WasapiOutRT.cs` (section 1 source),
adjusted so `activationParams` is an `IntPtr` to a `PROPVARIANT_BLOB` we allocate
via `Marshal.AllocHGlobal`/pin ourselves — needed here (unlike WasapiOutRT, which
passes `IntPtr.Zero`) because process-loopback activation requires passing the
`AUDIOCLIENT_ACTIVATION_PARAMS` blob.

```csharp
using System.Runtime.InteropServices;

namespace CallTap.Core.Interop;

internal static class NativeMethods
{
    internal const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = @"VAD\Process_Loopback";

    // IID_IAudioClient (audioclient.h) — passed as `riid` to request an
    // IAudioClient back from the activation.
    internal static readonly Guid IID_IAudioClient =
        new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    internal static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [In] IntPtr activationParams, // IntPtr to a pinned PROPVARIANT_BLOB, or IntPtr.Zero for default activation
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("ole32.dll")]
    internal static extern IntPtr CoTaskMemAlloc(nuint cb);

    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(IntPtr pv);
}
```

`PreserveSig = false` (matching WasapiOutRT's own declaration) means the CLR
auto-throws a COM exception on a non-`S_OK` HRESULT instead of returning it, which
is why the method signature above has no `HRESULT`/`int` return type despite the
native function returning one.

### 7.4 `Capture/ProcessLoopbackCapture.cs` (activation + capture loop shape)

```csharp
using System.Runtime.InteropServices;
using CallTap.Core.Interop;

namespace CallTap.Core.Capture;

public sealed class ProcessLoopbackCapture : IAudioTrackCapture, IDisposable
{
    public event EventHandler<AudioDataEventArgs>? DataAvailable;

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private volatile bool _running;

    /// Activates process-loopback capture for `targetPid` (+ child tree unless
    /// `excludeTree`) and starts a background capture thread. Mirrors
    /// SystemAudioRecorder.start(processes:outURL:) on macOS, minus the
    /// aggregate-device clock trick (WASAPI process loopback needs none — the
    /// activated IAudioClient IS the capture endpoint, no separate render-device
    /// clock has to be attached).
    public async Task StartAsync(int targetPid, bool excludeTree, string outWavPath)
    {
        var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
            {
                TargetProcessId = (uint)targetPid,
                ProcessLoopbackMode = excludeTree
                    ? PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                    : PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
            },
        };

        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr paramsPtr = Marshal.AllocCoTaskMem(size);
        IntPtr propvariantPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var propvariant = new PROPVARIANT_BLOB
            {
                vt = VT_BLOB,
                blob = new BLOB { cbSize = (uint)size, pBlobData = paramsPtr },
            };
            Marshal.StructureToPtr(propvariant, propvariantPtr, false);

            var handler = new ActivationCompletionHandler();
            NativeMethods.ActivateAudioInterfaceAsync(
                NativeMethods.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                NativeMethods.IID_IAudioClient,
                propvariantPtr,
                handler,
                out _);

            _audioClient = await handler.Completion.ConfigureAwait(false);
        }
        finally
        {
            Marshal.FreeCoTaskMem(paramsPtr);
            Marshal.FreeCoTaskMem(propvariantPtr);
        }

        // Process-loopback streams are always float32 stereo at the engine's
        // mix rate in practice (matches Microsoft sample guidance: don't call
        // GetMixFormat() on this activated client — some Windows builds return
        // E_NOTIMPL for it on the process-loopback endpoint, see
        // https://learn.microsoft.com/en-us/answers/questions/1125409/ ;
        // define the format explicitly instead).
        var format = new WAVEFORMATEX
        {
            wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = 2,
            nSamplesPerSec = 48000,
            wBitsPerSample = 32,
            nBlockAlign = (ushort)(2 * 4),
            nAvgBytesPerSec = 48000 * 2 * 4,
            cbSize = 0,
        };

        _audioClient!.Initialize(
            AUDCLNT_SHAREMODE.Shared,
            AUDCLNT_STREAMFLAGS.None, // no AUDCLNT_STREAMFLAGS_LOOPBACK here — the
                                      // process-loopback ACTIVATION TYPE already
                                      // implies a capture-of-render-stream; this
                                      // matches the Microsoft ApplicationLoopback
                                      // sample's Initialize call
            hnsBufferDuration: 200_0000, // 200ms, 100-ns units
            hnsPeriodicity: 0,           // 0 required in shared mode
            pFormat: ref format,
            audioSessionGuid: IntPtr.Zero);

        Guid iidCaptureClient = typeof(IAudioCaptureClient).GUID;
        _audioClient.GetService(iidCaptureClient, out var captureObj);
        _captureClient = (IAudioCaptureClient)captureObj;

        _running = true;
        _audioClient.Start();
        _captureThread = new Thread(() => CaptureLoop(outWavPath, format)) { IsBackground = true };
        _captureThread.Start();
    }

    private void CaptureLoop(string outWavPath, WAVEFORMATEX format)
    {
        using var writer = new Recording.WavFileWriter(outWavPath, format);
        while (_running)
        {
            _captureClient!.GetNextPacketSize(out uint framesAvailable);
            if (framesAvailable == 0) { Thread.Sleep(10); continue; }

            _captureClient.GetBuffer(out IntPtr buffer, out uint framesToRead,
                out uint flags, out _, out _);

            const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && framesToRead > 0)
            {
                writer.WriteFloat32Frames(buffer, framesToRead, format.nChannels);
                DataAvailable?.Invoke(this, new AudioDataEventArgs(buffer, framesToRead, format));
            }
            _captureClient.ReleaseBuffer(framesToRead);
        }
    }

    public void Stop()
    {
        _running = false;
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _audioClient?.Stop();
    }

    public void Dispose() => Stop();

    private sealed class ActivationCompletionHandler
        : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new();
        public Task<IAudioClient> Completion => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            activateOperation.GetActivateResult(out int hr, out object iface);
            if (hr != 0) { _tcs.TrySetException(Marshal.GetExceptionForHR(hr)!); return; }
            _tcs.TrySetResult((IAudioClient)iface);
        }
    }
}
```

Key documented gotcha baked into the comments above: a real, reported Windows 11
22H2 issue where calling `GetMixFormat()` on a process-loopback-activated
`IAudioClient` returns `E_NOTIMPL` (Microsoft Q&A thread found during research:
https://learn.microsoft.com/en-us/answers/questions/1125409/loopbackcapture-(-activateaudiointerfaceasync-with)-,
m_AudioClient->GetMixFormat failed with E_NOTIMPL). The contract therefore
specifies a fixed, known-good format (float32/48kHz/stereo) for `Initialize`
rather than trusting `GetMixFormat()` on this particular activated client, which
also matches the general shape of Microsoft's own ApplicationLoopback sample
(fixed capture format, not device-negotiated).

### 7.5 `MicrophoneCapture.cs` — NAudio, no raw interop

```csharp
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace CallTap.Core.Capture;

public sealed class MicrophoneCapture : IAudioTrackCapture, IDisposable
{
    public event EventHandler<AudioDataEventArgs>? DataAvailable;
    private WasapiCapture? _capture;

    public void Start(string outWavPath, string? deviceId = null)
    {
        MMDevice device;
        using var enumerator = new MMDeviceEnumerator();
        device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);

        _capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
        var writer = new WaveFileWriter(outWavPath, _capture.WaveFormat);
        _capture.DataAvailable += (_, e) =>
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            DataAvailable?.Invoke(this, new AudioDataEventArgs(e.Buffer, e.BytesRecorded, _capture.WaveFormat));
        };
        _capture.RecordingStopped += (_, __) => { writer.Dispose(); };
        _capture.StartRecording();
    }

    public void Stop() => _capture?.StopRecording();
    public void Dispose() => _capture?.Dispose();
}
```

`NAudio.Wasapi` `WasapiCapture` + `NAudio.CoreAudioApi` `MMDeviceEnumerator` are
the only NAudio types this project depends on for the microphone path — both are
part of stable, long-shipped NAudio public API (unrelated to the process-loopback
gap documented in section 1).

---

## 8. .csproj settings

### 8.1 `src/CallTap.Core/CallTap.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Platforms>x64</Platforms>
    <RootNamespace>CallTap.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.2.1" />
  </ItemGroup>
</Project>
```

### 8.2 `src/CallTap.Cli/CallTap.Cli.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <AssemblyName>calltap</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x64</Platforms>
    <!-- Release-only, opt-in via `dotnet publish -c Release -p:PublishSingleFile=true -->
    <PublishSingleFile Condition="'$(Configuration)'=='Release'">true</PublishSingleFile>
    <SelfContained Condition="'$(Configuration)'=='Release'">true</SelfContained>
    <RuntimeIdentifier Condition="'$(Configuration)'=='Release'">win-x64</RuntimeIdentifier>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\CallTap.Core\CallTap.Core.csproj" />
  </ItemGroup>
</Project>
```

### 8.3 `src/CallTap.Tray/CallTap.Tray.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms> <!-- NotifyIcon interop -->
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Platforms>x64</Platforms>
    <ApplicationIcon>Assets\tray.ico</ApplicationIcon>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\CallTap.Core\CallTap.Core.csproj" />
  </ItemGroup>
</Project>
```

### 8.4 `src/CallTap.Tests/CallTap.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <Platforms>x64</Platforms>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\CallTap.Core\CallTap.Core.csproj" />
  </ItemGroup>
</Project>
```

---

## 9. CI: GitHub Actions is the only real Windows test bed

There is no local Windows machine for this project — every audio-stack claim in
this contract must be verified by `windows-latest` CI, not by local `dotnet run`.
`.github/workflows/ci.yml` responsibilities:

1. `dotnet build` the whole solution on `windows-latest`.
2. `dotnet test` — but audio-hardware-dependent tests (anything that actually
   activates `IAudioClient`/`WasapiCapture`) must be tagged `[WindowsOnlyFact]`
   / skipped-with-reason when `windows-latest`'s hosted runner has no real audio
   device (GitHub-hosted Windows runners typically expose no functioning audio
   endpoint) — these tests assert "does not throw `E_ILLEGAL_METHOD_CALL`/COM
   marshaling errors", not "produces audible sound". A `HasWorkingAudioDevice()`
   probe (tries `MMDeviceEnumerator().GetDefaultAudioEndpoint`, catches
   `NAudio.CoreAudioApi.Interfaces.CoreAudioApiException` / general COM
   exceptions) gates these tests so CI stays green on hosted runners while still
   running for real on any self-hosted Windows runner added later.
3. On tag/release builds: `dotnet publish -c Release -r win-x64 --self-contained
   -p:PublishSingleFile=true` for `CallTap.Cli` and a normal framework-dependent
   (or self-contained, TBD at release time) build for `CallTap.Tray`, uploaded as
   workflow artifacts.
4. This is also where the project gets its **only real signal on whether the
   `ActivateAudioInterfaceAsync` interop layer actually compiles and marshals
   correctly** — treat any CI red on `CallTap.Core` as a hard architecture bug,
   not a flaky test.

---

## 10. What v1 deliberately leaves out

Explicitly out of scope, to keep the port honest about its actual current shape
(all of these are macOS-only features on the source project, or Windows-specific
complexity not worth v1's time):

- **Apple Notes destination.** `destinations.appleNotes` has no Windows
  equivalent (no OS-level Notes app with an AppleScript-like automation surface
  that's remotely as trivial). `destinations.py` implements Nextcloud + Notion
  only; the config key is parsed and silently ignored if `true`, with a one-time
  log warning, rather than removed from the schema (keeps `config.json` files
  portable between the two sibling projects even though this one field doesn't
  apply).
- **iPhone/cellular calls.** The Mac app's Continuity-style `com.apple.Phone`/
  `com.apple.TelephonyUtilities`/`com.apple.avconferenced` entries have no
  Windows analogue at all (Windows has no built-in cellular-relay call surface
  comparable to Continuity). v1's `apps` allowlist ships with **desktop VoIP apps
  only** (WhatsApp, Zoom, Teams, Discord); Phone Link / Your Phone Companion call
  audio routing is a possible future `apps` entry but is not researched or
  supported in v1.
- **Diarization threshold UI.** `diarizeThreshold` (default 0.6) remains a
  config-file-only knob, edited by hand or via a future settings field — v1's
  `SettingsView` does not expose a slider/control for it, matching the Mac
  app's current state (it's also config-only there, no UI control in
  `SettingsApp.swift` for this specific value as of this writing). Keeping this
  out of v1 avoids scope creep on a value that has no agreed-good UX (a raw
  0.0–1.0 float with no intuitive units) until real usage data from both
  platforms suggests one.
- **Signal Desktop, Telegram Desktop.** Present in the macOS `apps` example list
  (`org.whispersystems.signal-desktop`, `ru.keepcoder.Telegram`) but explicitly
  excluded from Windows v1's target list per this task's scope (WhatsApp/Zoom/
  Teams/Discord only). Adding them later is a one-line `apps` config change plus
  verifying their actual installed Windows exe names — no architecture change
  needed, since `ProcessMatcher`/`CallWatcher` are exe-name-driven and generic.
- **MSIX/Store packaging.** v1 ships as a signed portable EXE + PowerShell
  installer (mirrors the Mac app's plain `install.sh` + Homebrew-deps approach,
  not a notarized/sandboxed App Store-style distribution). `CallTap.Tray.appxmanifest`
  is scaffolded in `installer/` but unused until a packaging decision is made.
  MSIX sandboxing would also complicate raw process-loopback COM activation and
  cross-process file access to arbitrary `apps` targets, so it is a deliberate
  non-goal for v1, not an oversight.
- **Exclusive-mode WASAPI.** Both capture paths use `AUDCLNT_SHAREMODE.Shared`
  only, matching the Mac app's shared, non-exclusive approach to system audio
  (Process Taps never claim exclusive device ownership either) — v1 does not
  attempt exclusive-mode low-latency capture, since call-recording has no
  real-time monitoring requirement that would justify the added device-contention
  risk.
- **ARM64 Windows.** v1 targets `win-x64` only. `net8.0-windows`/NAudio both
  support `win-arm64`, but this project has no ARM64 Windows test coverage
  (CI matrix is `windows-latest`, which is x64), so ARM64 is left as a likely-easy,
  unverified future addition rather than a v1 claim.
- **Speaker-clip playback format parity beyond functional equivalence.** macOS
  writes `.m4a` snippets via ffmpeg (AAC); Windows v1 does the same (ffmpeg is a
  cross-platform installer dependency either way) rather than switching to a
  Windows-native codec — kept identical on purpose so `pipeline/` stays a shared
  mental model, not because `.m4a` is otherwise the natural Windows choice.

---

## 11. Open questions for the next contract revision

Flagged, not resolved, so implementation doesn't stall on them but they aren't
silently dropped either:

1. **Teams' actual audio-rendering process.** "New Teams" (`ms-teams.exe`) is
   WebView2-based; it needs to be verified during implementation whether call
   audio renders from `ms-teams.exe` itself or from a `msedgewebview2.exe` child
   process — if the latter, `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE`
   from the root PID should still catch it (WebView2 children are true child
   processes of the host), but this needs a real-machine (or CI-with-audio)
   confirmation before calling Teams support verified rather than assumed.
2. **Discord overlay/hardware-acceleration helper processes** — same
   child-process-tree question as Teams; expected to be covered by include-tree
   mode, unverified.
3. **Whether `Role.Communications` or `Role.Console`/`Role.Multimedia` is the
   right capture-endpoint role for `AudioSessionWatcher`** across all four target
   apps — v1's contract specifies checking `Communications` first with a
   `Console` fallback (section 4.3), but the actual per-app behavior needs
   verification once real Windows test hardware or a CI runner with audio is
   available.

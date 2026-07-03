# CallNotes for Windows — guide for AI assistants

You are probably helping someone install, test or debug this repo. Read this
first; it encodes what the first field tests actually taught us.

## What this is

Two-track call recorder + note pipeline, sibling of the macOS original
(https://github.com/michaelczesun/callnotes). `calltap.exe` (C#/.NET 8) records
per-app system audio via WASAPI **process loopback** plus the microphone as two
separate tracks; a Python pipeline transcribes (whisper.cpp / Parakeet / Groq),
separates speakers (sherpa-onnx), summarizes with the user's AI and writes a
Markdown note. `CallNotesTray` (WPF) is the tray UI. Architecture details:
`docs/contract.md`.

## Everything runs LOCALLY — these dependencies are required on the machine

The normal path is `installer/install.ps1`, which installs all of this. If you
have to do it manually (or PowerShell is broken — see below), you need:

- **.NET 8 SDK** — `winget install Microsoft.DotNet.SDK.8 --source winget`
- **Git** — `winget install Git.Git --source winget`
- **Python 3.11+** — `winget install Python.Python.3.12 --source winget`
  (runs the pipeline in `pipeline/`)
- **whisper.cpp Windows binary** (`whisper-cli.exe`) + a ggml model.
  Releases: https://github.com/ggerganov/whisper.cpp/releases (x64 zip — runs
  emulated on ARM64 Windows, that's fine).
  **Model choice matters:** `ggml-large-v3-turbo-q5_0.bin` (~550 MB) needs
  several GB of RAM. On machines/VMs with ≤ 8 GB, use **`ggml-small.bin`**
  (~470 MB, much lighter at runtime) — quality is fine for calls.
- Optional: **sherpa-onnx via pip** for speaker separation and the Parakeet
  transcriber (`pip install sherpa-onnx numpy`); models are downloaded by
  install.ps1 (or see the URLs in it).
- **No cloud is required.** Groq is an optional transcriber; the AI summary
  uses whatever the user configures (Claude CLI, any OpenAI-compatible URL,
  Ollama, or `off`).

## Field-tested facts and traps (learned the hard way)

- **`AUDCLNT_STREAMFLAGS_LOOPBACK` is mandatory** for the process-loopback
  `IAudioClient::Initialize`, despite the activation type already saying
  "loopback". Without it: `0x88890021` (fixed in v0.1.1 — do not "simplify"
  it away again).
- **`calltap record` without `--exe` is silent BY DESIGN** (it targets its own
  PID). To prove loopback works: play audio in some app, then
  `calltap record --exe <processname> --out <dir> --seconds 10`.
- **Windows PowerShell can be broken** on stripped-down images (error: ".NET
  Framework v4.0.30319 is not installed"). Everything here can be driven from
  `cmd`/bash instead; `installer/install.ps1` then can't run — follow its steps
  manually (they are commented).
- **winget msstore source may fail** with certificate error `0x8a15005e` —
  always pass `--source winget`.
- **ARM64 detection lies in emulated shells:** `PROCESSOR_ARCHITECTURE` says
  `AMD64` inside x64-emulated bash/cmd. Check the OS, not the shell. Native
  ARM64 builds of calltap work (verified, PE machine 0xAA64).
- CI (`.github/workflows/build.yml`, windows-latest) is the compile gate;
  the first runtime field test was 2026-07-03 in a Win11 ARM64 VM: build ✓,
  `procs` ✓, `setup` ✓, loopback recording with real audio ✓.

## Build & smoke test

```
dotnet build src\CallTap\CallTap.csproj -c Release
dotnet build src\CallNotesTray\CallNotesTray.csproj -c Release
<binpath>\calltap.exe procs        # lists audio sessions; MIC = mic in use
<binpath>\calltap.exe setup        # self-test incl. process-loopback activation
```

Config lives at `%APPDATA%\callnotes\config.json` (create from
`config.example.json`); file formats (`current-call.json`, `levels.json`,
`state/pending/*.json`) are identical to the macOS original by contract.

## House rules

- License is PolyForm Noncommercial — no commercial use/selling.
- Never commit user data, API keys or recordings; `config.json` stays local.
- Keep the file formats in lockstep with the Mac repo — they are one product.

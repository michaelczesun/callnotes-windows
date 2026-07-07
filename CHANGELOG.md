# Changelog

All notable changes to CallNotes for Windows. Newest first.
Format follows [Keep a Changelog](https://keepachangelog.com); full notes per
version are on the [Releases page](https://github.com/michaelczesun/callnotes-windows/releases).

This is the **experimental** Windows sibling of
[CallNotes for macOS](https://github.com/michaelczesun/callnotes).

## 0.3.1 — 2026-07-07
### Fixed
- Failed recordings now show **why** in the tray (parity with the Mac).
- `FinishRecording` is now exception-safe — if finalizing threw (e.g. disk full), the
  watcher used to keep the dead recording "active" and hang in an infinite retry loop.
- Start-error backoff is now **per-app** — a transient failure on one app no longer
  blocks recording a real call from a different app for up to 60 s.

## 0.3.0 — 2026-07-06
### Added
- **Live mic monitor** + **browser-call capture** ("Always record this app"), ported
  from the Mac's 1.3.0.

## 0.2.0 — 2026-07-04
### Added
- Full macOS-style **settings panel** (segmented controls, note-content/destination
  checkboxes, storage paths, ntfy), active-call card with waveforms, dark theme + app
  icon; panel dismisses on focus loss / Esc.
- Idle hint (recording starts on an *active* call); collapsible FAQ; uninstall section.

## 0.1.1 — 2026-07-03
### Fixed (first VM field test)
- Process loopback returned `0x88890021` — `IAudioClient::Initialize` for process
  loopback **requires** `AUDCLNT_STREAMFLAGS_LOOPBACK`. With the fix, two-track
  capture was proven (440 Hz tone at −0.1 dBFS + mic in parallel); native ARM64 build.

## 0.1.0 — 2026-07-03
### Added
- First public cut: two-track capture via **WASAPI process loopback**, the full
  watch-loop ported from the Mac, the shared Python pipeline, tray app, installer and
  windows-latest CI. Experimental — looking for testers.

# Changelog

All notable changes to CallNotes for Windows. Newest first.
Format follows [Keep a Changelog](https://keepachangelog.com); full notes per
version are on the [Releases page](https://github.com/michaelczesun/callnotes-windows/releases).

This is the **experimental** Windows sibling of
[CallNotes for macOS](https://github.com/michaelczesun/callnotes).

## 0.3.3 — 2026-09-20
### Fixed
- **Speaker diarization no longer invents dozens of speakers** (parity with Mac
  1.3.4). Echo/noise/artifacts on the tapped caller track could split into many tiny
  bogus clusters (97 on one real call). Speakers are now weighted by total talk time;
  only real participants (>=4 s and >=3 % of speech) count, the rest fold into the
  nearest real speaker, and <=1 significant speaker collapses to a single peer.

## 0.3.2 — 2026-08-06
### Fixed
- **Summarizer fallback + alert** (parity with Mac): if the Claude CLI summarizer
  fails (commonly an expired login), fall back to the configured API (e.g. Groq)
  so a summary is still produced, and ntfy-alert either way.

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

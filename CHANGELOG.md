# Changelog

All notable changes to CallNotes for Windows. Newest first.
Format follows [Keep a Changelog](https://keepachangelog.com); full notes per
version are on the [Releases page](https://github.com/michaelczesun/callnotes-windows/releases).

This is the **experimental** Windows sibling of
[CallNotes for macOS](https://github.com/michaelczesun/callnotes).

## 0.4.0 — 2026-10-11
Parity with Mac 1.5.0: a reliability and resource overhaul. The numbers below were
measured on real call recordings (11-138 min) with the Mac reference implementation;
the Windows pipeline runs the same code.
### Fixed
- **Parakeet no longer runs out of memory on long calls.** The whole recording was
  fed into the model at once, so memory grew with call length. Parakeet now decodes
  speech in chunks of up to 20 s found by a voice-activity detector (silero VAD) and
  skips silence. A 33-min call takes ~4 min end to end instead of ~52, at ~1 GB peak
  memory, on 2 CPU threads (`CALLNOTES_THREADS`).
- **Speaker detection no longer invents speakers.** New engine: one voice
  fingerprint per speech segment, grouped by similarity. 1:1 calls that reported 3-6
  speakers now report 1; a real 5-person team call reports 5. It runs in 8 s to
  2.5 min instead of 5-27 min. `diarizeThreshold` is now a similarity threshold
  (default `0.4`, higher = more speakers; calibrated range 0.30-0.45). If your
  `config.json` still has the old default `0.6`, set it to `0.4`.
- An honest log message when Parakeet fails (out of memory / model missing / other,
  with the exit code and a pointer to `parakeet.log`) instead of one generic line.
  A successful run now logs its stats line (chunks, speech minutes, real-time factor).
### Changed
- Parakeet, speaker detection and whisper.cpp run at **below-normal priority**, so
  the PC stays responsive while a call is processed.
- `install.ps1` downloads the 0.6 MB silero VAD model; the pyannote segmentation
  model is no longer needed. Re-run `installer\install.ps1` once on an existing
  setup (without the VAD model the pipeline falls back to an energy estimate).
### Measured, not added
- Noise filters (FFT, NLM, loudness normalisation) made recognition **worse** on
  real call noise (8-12 % word errors vs 2-7 % without), so there is none. What
  read like "noise" in notes was Whisper inventing sentences in silence; Parakeet
  does not (0 vs 753 repetition loops on the same 33-min track).

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

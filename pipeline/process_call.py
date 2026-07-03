#!/usr/bin/env python3
# process_call.py — verarbeitet eine calltap-Anrufaufnahme vollautomatisch:
#   2 Spuren (mic.wav/system.wav) -> whisper-cli(.exe)/Groq -> Dialog-Transkript mit
#   Sprechern -> Claude-Zusammenfassung (optional) -> Notiz -> m4a-Archiv -> Spiegel-
#   Kopie -> ntfy-Push.
# Windows-Port von callnotes/process-call.sh (v2.0.0) — GESAMTE Semantik uebernommen:
# PID-Lock, Stumm-Spur-Skip, whisper-cli(.exe) lokal ODER Groq (User-Agent-Header!),
# Diarisierung >1 Sprecher -> Speaker-Prefix DE/EN, Schnipsel-Export, pending.json,
# KI claude-CLI/OpenAI-kompatibel/off mit DE+EN-Prompts und Sektionen laut
# noteSections, ZUORDNUNG/MAPPING-Parsing, Notiz-Bau mit Kollisions-Suffix,
# MOC optional, m4a via ffmpeg (L=mic/R=system), Nextcloud-WebDAV, Notion, ntfy,
# mirror-sync, processing.json-Phasen DE/EN, failed/-Handling.
#
# Aufruf vom calltap-watch-Daemon (CallTap.Tray/CallTap.Cli) oder manuell:
#   python process_call.py <rec-dir>
# Konfiguration: %APPDATA%\callnotes\config.json (Override: CALLNOTES_CONFIG env var)
#
# Unterschiede zum Mac-Original (bewusst, siehe docs/contract.md):
#   - Audiospuren heissen mic.wav/system.wav statt mic.caf/system.caf (WASAPI liefert
#     PCM/Float-WAV, kein CAF-Aequivalent auf Windows).
#   - Kein Apple-Notes-Ziel (destinations.appleNotes wird geparst, aber ignoriert +
#     einmalig geloggt — kein Windows-Aequivalent, siehe contract.md Abschnitt 10).
#   - Kein osascript/launchctl; Lock ist PID-basiert ueber os.kill(pid, 0)-Aequivalent
#     (Windows: psutil-frei via ctypes/OpenProcess, siehe _pid_alive()).
#   - Pfade ausschliesslich ueber pathlib + os.path.expandvars (%USERPROFILE% etc.),
#     kein os.path.expanduser("~") noetig (Config liefert vollstaendige Windows-Pfade).
#   - Claude-CLI-Timeout ueber subprocess + Thread-Watchdog statt bash "kill -9".
from __future__ import annotations

import ctypes
import html
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import time
import unicodedata
import urllib.request
from datetime import datetime
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent


# ============================================================================
# Hilfsfunktionen: Zeit/Log, Pfad-Expansion, Prozess-Leben-Check
# ============================================================================

def say(msg: str) -> None:
    print(f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] {msg}", flush=True)


def expand(p: str | None, default: str = "") -> str:
    """Windows-Env-Vars (%USERPROFILE%, %APPDATA%, %LOCALAPPDATA%, ...) expandieren.
    Aequivalent zu os.path.expanduser() im Mac-Original, aber fuer Windows-Notation."""
    raw = p or default
    if not raw:
        return ""
    return os.path.expandvars(raw)


def _pid_alive(pid: int) -> bool:
    """Windows-Aequivalent zu 'kill -0 PID' (Mac-Original) ohne Fremdabhaengigkeit
    (kein psutil noetig): OpenProcess schlaegt fehl, wenn der Prozess nicht mehr lebt."""
    if pid <= 0:
        return False
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    handle = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if handle:
        ctypes.windll.kernel32.CloseHandle(handle)
        return True
    return False


def ntfy(msg: str, title: str, ntfy_url: str) -> None:
    if not ntfy_url:
        return
    try:
        req = urllib.request.Request(
            ntfy_url,
            data=msg.encode("utf-8"),
            headers={"Title": title, "User-Agent": "CallNotes"},
            method="POST",
        )
        urllib.request.urlopen(req, timeout=10).read()
    except Exception:
        pass  # ntfy ist best-effort, darf die Verarbeitung nie stoppen


def run(cmd: list[str], **kw) -> subprocess.CompletedProcess:
    """subprocess.run mit versteckter Konsole (Windows) + sinnvollen Defaults."""
    creationflags = 0
    if os.name == "nt":
        creationflags = subprocess.CREATE_NO_WINDOW
    kw.setdefault("capture_output", True)
    return subprocess.run(cmd, creationflags=creationflags, **kw)


def which(name: str) -> str | None:
    return shutil.which(name)


# ============================================================================
# Config laden — key-for-key wie config.json (contract.md Abschnitt 3.1)
# ============================================================================

class Config:
    def __init__(self, cfg_path: Path):
        with open(cfg_path, encoding="utf-8") as f:
            d = json.load(f)
        self.raw = d

        self.base = Path(expand(d.get("outDir"), r"%USERPROFILE%\CallNotes"))
        self.notes_dir = Path(expand(d.get("notesDir"), str(self.base / "notes")))
        self.audio_dir = Path(expand(d.get("audioDir"), str(self.base / "audio")))
        self.mirror_dir = expand(d.get("mirrorDir"))
        self.model = expand(d.get("whisperModel"))
        self.claude_bin = expand(d.get("claudeBin"))
        self.ntfy_url = d.get("ntfyUrl") or ""
        self.language = d.get("language") or "de"
        self.speaker_self = d.get("speakerSelf") or ""
        self.speaker_peer = d.get("speakerPeer") or ""
        self.context = d.get("context") or ""
        self.moc_on = bool(d.get("notesMoc", True))
        self.diarize_on = bool(d.get("diarize", True))
        self.dia_threshold = float(d.get("diarizeThreshold") or 0.6)
        self.venv_python = expand(
            d.get("venvPython"), r"%LOCALAPPDATA%\callnotes\venv\Scripts\python.exe"
        )
        self.transcriber = d.get("transcriber") or "local"
        self.groq_api_key = d.get("groqApiKey") or ""
        self.summarizer = d.get("summarizer") or "claude"
        self.sum_url = (d.get("summarizerUrl") or "").rstrip("/")
        self.sum_model = d.get("summarizerModel") or ""
        self.sum_key = d.get("summarizerApiKey") or ""
        self.sections = d.get("noteSections") or ["kurzfassung", "besprochen", "todos"]

        dest = d.get("destinations") or {}
        self.dest_apple_notes = bool(dest.get("appleNotes"))
        self.dest_nextcloud = bool(dest.get("nextcloud"))
        self.dest_notion = bool(dest.get("notion"))
        self.nc_url = d.get("nextcloudUrl") or ""
        self.nc_user = d.get("nextcloudUser") or ""
        self.nc_pass = d.get("nextcloudAppPass") or ""
        self.notion_token = d.get("notionToken") or ""
        self.notion_parent = d.get("notionParent") or ""


def load_secrets_env(path: Path) -> dict[str, str]:
    """Minimaler .env-Parser (KEY=VALUE je Zeile), Aequivalent zu 'source secrets.env'
    im Mac-Original — hier bewusst nicht 'source'n (kein Shell-Kontext), nur die
    Variablen einlesen, die wir tatsaechlich brauchen."""
    out: dict[str, str] = {}
    if not path.is_file():
        return out
    try:
        for line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            v = v.strip().strip('"').strip("'")
            out[k.strip()] = v
    except OSError:
        pass
    return out


# ============================================================================
# Sprach-Texte (DE/EN) — 1:1 wie process-call.sh Sprachweiche
# ============================================================================

class Texts:
    def __init__(self, cfg: Config):
        de = cfg.language == "de"
        self.de = de
        self.self_label = cfg.speaker_self or ("Ich" if de else "Me")
        self.peer_label = cfg.speaker_peer or ("Gesprächspartner" if de else "Caller")
        self.speaker_prefix = "Sprecher" if de else "Speaker"
        self.ph_trans = "Transkription läuft…" if de else "Transcribing…"
        self.ph_dia = "Sprecher-Erkennung…" if de else "Detecting speakers…"
        self.ph_ai = "KI-Zusammenfassung…" if de else "AI summary…"
        self.ph_store = "Archiv & Ablage…" if de else "Archiving & delivery…"
        self.transcript_hdr = "Transkript" if de else "Transcript"
        self.call_word = "Telefonat" if de else "Call"
        self.audio_note = "Audio-Archiv" if de else "Audio archive"
        self.left = "links" if de else "left"
        self.right = "rechts" if de else "right"


# ============================================================================
# Prozessing-Status fuer Tray-App (state/processing.json)
# ============================================================================

class ProcessingState:
    def __init__(self, base: Path, stamp: str):
        self.dir = base / "state"
        self.stamp = stamp

    def phase(self, text: str) -> None:
        try:
            self.dir.mkdir(parents=True, exist_ok=True)
            (self.dir / "processing.json").write_text(
                json.dumps({"stamp": self.stamp, "phase": text}), encoding="utf-8"
            )
        except OSError:
            pass

    def done(self) -> None:
        try:
            (self.dir / "processing.json").unlink(missing_ok=True)
        except OSError:
            pass


class ProcessingFailed(Exception):
    pass


# ============================================================================
# PID-Lock: nur eine Verarbeitung gleichzeitig (whisper + claude sind RAM-/CPU-hungrig)
# ============================================================================

class ProcessLock:
    """Lock-Verzeichnis mit pid-Datei, Windows-Aequivalent zum 'mkdir'-Lock des
    Mac-Originals (mkdir ist auf beiden Systemen atomar). Lebt der Halter noch,
    wird beliebig lange gewartet (lange Calls!); nur ein toter Halter wird
    uebernommen."""

    def __init__(self, base: Path):
        self.lock_dir = base / ".process.lock"
        self.acquired = False

    def acquire(self) -> None:
        while True:
            try:
                self.lock_dir.mkdir(parents=False, exist_ok=False)
                break
            except FileExistsError:
                pid_file = self.lock_dir / "pid"
                holder = ""
                try:
                    holder = pid_file.read_text(encoding="utf-8").strip()
                except OSError:
                    pass
                holder_pid = int(holder) if holder.isdigit() else 0
                if not holder_pid or not _pid_alive(holder_pid):
                    say(f"Lock-Halter ({holder or 'unbekannt'}) lebt nicht mehr — uebernehme")
                    shutil.rmtree(self.lock_dir, ignore_errors=True)
                    continue
                time.sleep(10)
        (self.lock_dir / "pid").write_text(str(os.getpid()), encoding="utf-8")
        self.acquired = True

    def release(self) -> None:
        if self.acquired:
            shutil.rmtree(self.lock_dir, ignore_errors=True)
            self.acquired = False


# ============================================================================
# Fehlerpfad: Aufnahme nach failed/ verschieben + ntfy + processing.json weg
# ============================================================================

def fail(msg: str, base: Path, rec: Path, state: ProcessingState, cfg: Config) -> None:
    say(f"FEHLER: {msg}")
    state.done()
    failed_dir = base / "failed"
    failed_dir.mkdir(parents=True, exist_ok=True)
    try:
        if failed_dir not in rec.parents and rec.is_dir():
            shutil.move(str(rec), str(failed_dir / rec.name))
    except OSError:
        pass
    ntfy(
        f"Anruf-Verarbeitung fehlgeschlagen: {msg} — Audio liegt in {failed_dir / rec.name}",
        "Anruf-Notiz FEHLER",
        cfg.ntfy_url,
    )
    raise ProcessingFailed(msg)


# ============================================================================
# Transkription: Spur -> 16kHz-WAV -> whisper-cli(.exe) oder Groq
# ============================================================================

def ffmpeg_bin() -> str:
    return which("ffmpeg") or "ffmpeg"


def whisper_cli_bin() -> str:
    # Windows-Binary heisst meist whisper-cli.exe; PATH-Suche findet beides.
    return which("whisper-cli.exe") or which("whisper-cli") or "whisper-cli.exe"


def transcribe_track(
    rec: Path,
    src_name: str,
    out_label: str,
    keep_wav: bool,
    cfg: Config,
    groq_key: str,
) -> bool:
    """Portiert process-call.sh's transcribe(). Rueckgabe True = Erfolg (JSON vorhanden)."""
    src = rec / src_name
    wav = rec / f"{out_label}.16k.wav"
    if not (src.is_file() and src.stat().st_size > 0):
        say(f"  Spur {src_name} fehlt/leer — uebersprungen")
        return False

    r = run([ffmpeg_bin(), "-hide_banner", "-loglevel", "error", "-y",
             "-i", str(src), "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", str(wav)])
    if r.returncode != 0:
        return False

    # Praktisch stumme Spur nicht transkribieren (Whisper halluziniert sonst)
    vol_out = run([ffmpeg_bin(), "-i", str(wav), "-af", "volumedetect", "-f", "null", "-"]).stderr
    vol_out = vol_out.decode("utf-8", errors="ignore") if isinstance(vol_out, bytes) else (vol_out or "")
    m = re.search(r"max_volume:\s*([-\d.]+)", vol_out)
    max_volume = float(m.group(1)) if m else -99.0
    if max_volume < -50:
        say(f"  Spur {src_name} ist stumm (max {max_volume}dB) — uebersprungen")
        wav.unlink(missing_ok=True)
        return False

    json_out = rec / f"{out_label}.json"

    # Groq-Cloud-Whisper (optional, schneller bei langen Calls) — Fallback lokal
    if cfg.transcriber == "groq" and groq_key:
        groq_json = rec / f"{out_label}.groq.json"
        try:
            groq_ok = _groq_transcribe(wav, groq_json, groq_key, cfg.language, rec)
        except Exception as e:
            groq_ok = False
            (rec / "groq.log").open("a", encoding="utf-8").write(f"{e}\n")
        if groq_ok:
            try:
                d = json.load(open(groq_json, encoding="utf-8"))
                segs = [
                    {
                        "offsets": {"from": int(s["start"] * 1000), "to": int(s["end"] * 1000)},
                        "text": s.get("text", ""),
                    }
                    for s in d.get("segments", [])
                ]
                json.dump({"transcription": segs}, open(json_out, "w", encoding="utf-8"), ensure_ascii=False)
            finally:
                groq_json.unlink(missing_ok=True)
        else:
            say("  Groq nicht erreichbar — lokal (whisper.cpp)")

    if not (json_out.is_file() and json_out.stat().st_size > 0):
        run([
            whisper_cli_bin(), "-m", cfg.model, "-l", cfg.language, "-np",
            "-oj", "-of", str(rec / out_label), "-f", str(wav),
        ])

    if not keep_wav:
        wav.unlink(missing_ok=True)

    return json_out.is_file() and json_out.stat().st_size > 0


def _groq_transcribe(wav: Path, out_json: Path, groq_key: str, language: str, rec: Path) -> bool:
    """Groq-API-Call via urllib multipart (User-Agent-Header ist Pflicht, sonst 403
    von manchen API-Firewalls — 1:1 aus dem Mac-Original uebernommen)."""
    boundary = "----CallNotesBoundary7f3a9c"
    body = bytearray()

    def add_field(name: str, value: str):
        body.extend(f"--{boundary}\r\n".encode())
        body.extend(f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode())
        body.extend(f"{value}\r\n".encode())

    add_field("model", "whisper-large-v3-turbo")
    add_field("language", language)
    add_field("temperature", "0")
    add_field("response_format", "verbose_json")

    body.extend(f"--{boundary}\r\n".encode())
    body.extend(f'Content-Disposition: form-data; name="file"; filename="{wav.name}"\r\n'.encode())
    body.extend(b"Content-Type: audio/wav\r\n\r\n")
    body.extend(wav.read_bytes())
    body.extend(b"\r\n")
    body.extend(f"--{boundary}--\r\n".encode())

    req = urllib.request.Request(
        "https://api.groq.com/openai/v1/audio/transcriptions",
        data=bytes(body),
        headers={
            "Authorization": f"Bearer {groq_key}",
            "Content-Type": f"multipart/form-data; boundary={boundary}",
            "User-Agent": "CallNotes",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            out_json.write_bytes(resp.read())
        return out_json.is_file() and out_json.stat().st_size > 0
    except Exception as e:
        with open(rec / "groq.log", "a", encoding="utf-8") as f:
            f.write(f"{e}\n")
        return False


# ============================================================================
# Diarisierung (sherpa-onnx via venv-Python, subprocess wie im Original)
# ============================================================================

def run_diarization(rec: Path, cfg: Config, texts: Texts, state: ProcessingState) -> int:
    """Rueckgabe: N_SPEAKERS (>=1). Schreibt rec/diarization.json bei Erfolg."""
    dia_json = rec / "diarization.json"
    n_speakers = 1
    sys_wav = rec / "system.16k.wav"
    venv_py = Path(cfg.venv_python)

    if cfg.diarize_on and sys_wav.is_file() and sys_wav.stat().st_size > 0 and venv_py.is_file():
        state.phase(texts.ph_dia)
        say("Diarisierung (sherpa-onnx) …")
        diarize_script = SCRIPT_DIR / "diarize.py"
        try:
            with open(dia_json, "w", encoding="utf-8") as out_f, \
                 open(rec / "diarize.log", "a", encoding="utf-8") as log_f:
                r = subprocess.run(
                    [str(venv_py), str(diarize_script), str(sys_wav), str(cfg.dia_threshold)],
                    stdout=out_f, stderr=log_f,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                )
            if r.returncode != 0:
                dia_json.unlink(missing_ok=True)
        except OSError:
            dia_json.unlink(missing_ok=True)

        if dia_json.is_file() and dia_json.stat().st_size > 0:
            try:
                n_speakers = json.load(open(dia_json, encoding="utf-8")).get("speakers", 1)
                say(f"  Gegenseite: {n_speakers} Sprecher erkannt")
            except (OSError, json.JSONDecodeError):
                n_speakers = 1

    sys_wav.unlink(missing_ok=True)
    return n_speakers


# ============================================================================
# KI-Zusammenfassung: Claude-CLI / OpenAI-kompatibel / off, DE+EN-Prompts
# ============================================================================

def build_prompt(
    dialog: str, texts: Texts, cfg: Config, n_speakers: int, participants: str, sections: list[str]
) -> str:
    extra = ""
    structure = ""
    if texts.de:
        if n_speakers > 1:
            extra = (
                f'Auf der Gegenseite wurden {n_speakers} verschiedene Stimmen erkannt '
                f'("{texts.speaker_prefix} 1..{n_speakers}", nummeriert nach erster Wortmeldung).'
            )
            if participants:
                extra += f" Laut {texts.self_label} waren dabei: {participants}."
            extra += (
                f" Haenge ANS ENDE deiner Antwort eine einzelne Zeile an: "
                f"ZUORDNUNG: {texts.speaker_prefix} 1=<Name oder ?>; {texts.speaker_prefix} 2=<Name oder ?>; ... "
                f"— nutze Namen NUR wenn sie sich klar aus Anreden/Selbstvorstellungen im Transkript ergeben, sonst ?."
            )
        elif participants:
            extra = f"Gespraechspartner laut {texts.self_label}: {participants}."

        if "kurzfassung" in sections:
            structure += "\n## Kurzfassung\n2-4 Saetze: mit wem (falls erkennbar), worum ging es, Ergebnis.\n"
        if "besprochen" in sections:
            structure += "\n## Besprochen\n- die wesentlichen Punkte, kompakt\n"
        if "todos" in sections:
            structure += (
                "\n## Zusagen & To-dos\n"
                f"- [ ] (selbst) was {texts.self_label} zugesagt hat / tun muss\n"
                "- [ ] (gegenseite) was der andere zugesagt hat\n"
                '(nur echte Zusagen; wenn keine: "- keine")\n\n'
                "## Offene Punkte\n"
                '- was unklar blieb oder Follow-up braucht (wenn nichts: "- keine")\n'
            )
        if "followup" in sections:
            structure += (
                "\n## Follow-up-Mail (Entwurf)\n"
                "Kurzer, freundlicher Mail-Entwurf an die Gegenseite: Dank, Vereinbartes, "
                "naechste Schritte. Kein Betreff-Gedoens, direkt der Text.\n"
            )

        context_part = f" Kontext: {cfg.context}" if cfg.context else ""
        header = (
            f'Du bekommst das Transkript eines Telefonats. "{texts.self_label}" = die Person, '
            f'deren Notiz das ist;\n"{texts.peer_label}" bzw. "{texts.speaker_prefix} N" = die '
            f'Personen am anderen Ende.{context_part}\n'
            f"{extra}\n"
            "Whisper-Fehler (Namen, Zahlen, Fachbegriffe) im Kontext still korrigieren; akustisch\n"
            "Unklares als [unklar] markieren, NIE raten.\n\n"
            "Antworte NUR mit Markdown in exakt dieser Struktur (keine Vorrede, kein Codeblock):\n\n"
            "# Telefonat <Name der Gegenseite oder Thema> — <TT.MM.>\n"
            f"{structure}\n"
            "Halte dich strikt ans Transkript, erfinde nichts dazu.\n\n"
            "--- TRANSKRIPT ---\n"
        )
    else:
        if n_speakers > 1:
            extra = (
                f'{n_speakers} distinct voices were detected on the far end '
                f'("{texts.speaker_prefix} 1..{n_speakers}", numbered by first utterance).'
            )
            if participants:
                extra += f" According to {texts.self_label} the participants were: {participants}."
            extra += (
                f" Append ONE single line at the END of your answer: "
                f"MAPPING: {texts.speaker_prefix} 1=<name or ?>; {texts.speaker_prefix} 2=<name or ?>; ... "
                f"— use a name ONLY when it clearly follows from greetings/self-introductions in the "
                f"transcript, otherwise ?."
            )
        elif participants:
            extra = f"Participants according to {texts.self_label}: {participants}."

        if "kurzfassung" in sections:
            structure += "\n## Summary\n2-4 sentences: who (if identifiable), what it was about, outcome.\n"
        if "besprochen" in sections:
            structure += "\n## Discussed\n- the key points, concise\n"
        if "todos" in sections:
            structure += (
                "\n## Commitments & to-dos\n"
                f"- [ ] (self) what {texts.self_label} committed to / must do\n"
                "- [ ] (other side) what the other party committed to\n"
                '(only real commitments; if none: "- none")\n\n'
                "## Open items\n"
                '- anything unclear or needing follow-up (if nothing: "- none")\n'
            )
        if "followup" in sections:
            structure += (
                "\n## Follow-up email (draft)\n"
                "Short, friendly draft to the other party: thanks, what was agreed, next steps. "
                "No subject line, just the body.\n"
            )

        context_part = f" Context: {cfg.context}" if cfg.context else ""
        header = (
            f'You are given the transcript of a phone call. "{texts.self_label}" = the person this '
            f'note belongs to;\n"{texts.peer_label}" / "{texts.speaker_prefix} N" = the people on the '
            f'other end.{context_part}\n'
            f"{extra}\n"
            "Silently correct obvious transcription errors (names, numbers, jargon) from context; mark\n"
            "acoustically unclear parts as [unclear], NEVER guess.\n\n"
            "Reply ONLY with Markdown in exactly this structure (no preamble, no code block):\n\n"
            "# Call with <name of the other party or topic> — <MM/DD>\n"
            f"{structure}\n"
            "Stick strictly to the transcript; invent nothing.\n\n"
            "--- TRANSCRIPT ---\n"
        )

    return header + dialog


def summarize_openai(prompt: str, cfg: Config, rec: Path) -> str | None:
    """Jede OpenAI-kompatible Chat-API: OpenAI, Groq, OpenRouter, Ollama (lokal), …"""
    payload = {
        "model": cfg.sum_model,
        "temperature": 0.2,
        "messages": [{"role": "user", "content": prompt}],
    }
    headers = {"Content-Type": "application/json", "User-Agent": "CallNotes"}
    if cfg.sum_key:
        headers["Authorization"] = f"Bearer {cfg.sum_key}"
    req = urllib.request.Request(
        cfg.sum_url + "/chat/completions",
        data=json.dumps(payload).encode(),
        headers=headers,
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            data = json.load(resp)
        text = ((data.get("choices") or [{}])[0].get("message", {}) or {}).get("content", "").strip()
        if not text:
            return None
        if text.startswith("```"):
            text = text.strip("`")
            if text.lower().startswith("markdown"):
                text = text[len("markdown"):]
            text = text.strip()
        return text
    except Exception as e:
        with open(rec / "summarizer.log", "a", encoding="utf-8") as f:
            f.write(f"{e}\n")
        return None


def summarize_claude(prompt: str, claude_bin: str, rec: Path) -> str | None:
    """Claude Code CLI mit 300s-Timeout (Watchdog statt bash 'kill -9')."""
    try:
        proc = subprocess.Popen(
            [claude_bin, "-p", "--model", "sonnet"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
    except OSError:
        return None

    try:
        out, err = proc.communicate(input=prompt.encode("utf-8"), timeout=300)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.communicate()
        say("  Claude-Timeout (300s)")
        return None

    if err:
        with open(rec / "claude.log", "a", encoding="utf-8") as f:
            f.write(err.decode("utf-8", errors="ignore"))
    if proc.returncode != 0:
        return None
    return out.decode("utf-8", errors="ignore")


def make_summary(
    dialog: str, cfg: Config, texts: Texts, n_speakers: int, participants: str, rec: Path
) -> str | None:
    if cfg.summarizer == "off":
        return None
    if cfg.summarizer == "openai":
        if not (cfg.sum_url and cfg.sum_model):
            say("  KI-Zusammenfassung: URL/Modell fehlen (Einstellungen)")
            return None
    else:
        claude_bin = cfg.claude_bin or which("claude") or ""
        if not claude_bin or not (which(claude_bin) or Path(claude_bin).is_file()):
            found = which("claude")
            if not found:
                return None
            claude_bin = found
        cfg.claude_bin = claude_bin

    prompt = build_prompt(dialog, texts, cfg, n_speakers, participants, cfg.sections)
    (rec / "prompt.txt").write_text(prompt, encoding="utf-8")

    if cfg.summarizer == "openai":
        text = summarize_openai(prompt, cfg, rec)
        if text is None:
            say(f"  KI-API nicht erreichbar ({cfg.sum_url}) — Details in summarizer.log")
            return None
    else:
        text = summarize_claude(prompt, cfg.claude_bin, rec)
        if text is None:
            return None

    if not re.search(r"^#", text, re.MULTILINE):
        return None
    return text


# ============================================================================
# Notiz-Bau: Slug, Kollisions-Suffix, Frontmatter, MOC
# ============================================================================

def slugify(title: str) -> str:
    s = re.sub(r"—.*$", "", title)
    s = (
        s.replace("ä", "ae").replace("ö", "oe").replace("ü", "ue")
        .replace("Ä", "ae").replace("Ö", "oe").replace("Ü", "ue")
        .replace("ß", "ss")
    )
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode("ascii")
    s = s.lower()
    s = re.sub(r"\btelefonat\b", "", s)
    s = re.sub(r"\bcall with\b", "", s)
    s = re.sub(r"\bcall\b", "", s)
    s = re.sub(r"\bunbekannt\b", "", s)
    s = re.sub(r"\bunknown\b", "", s)
    s = re.sub(r"[^a-z0-9]+", "-", s)
    s = re.sub(r"^-+|-+$", "", s)
    s = re.sub(r"-+", "-", s)
    return s[:40].rstrip("-")


def build_note_path(notes_dir: Path, date_part: str, time_part: str, slug: str) -> Path:
    note = notes_dir / f"{date_part}-{time_part}-anruf-{slug}.md"
    n = 2
    while note.exists():
        note = notes_dir / f"{date_part}-{time_part}-anruf-{slug}-{n}.md"
        n += 1
    return note


def update_moc(moc: Path, note_base: str, title: str, date_part: str, texts: Texts) -> None:
    if texts.de:
        moc_title, moc_desc, moc_list = "Anrufe MOC", "Automatische Telefonat-Notizen (CallNotes).", "Anrufe"
    else:
        moc_title, moc_desc, moc_list = "Calls MOC", "Automatic call notes (CallNotes).", "Calls"

    if not moc.exists():
        moc.write_text(
            "---\n"
            "type: MOC\n"
            "tags: [moc, call]\n"
            f"updated: {date_part}\n"
            "---\n\n"
            f"# {moc_title}\n\n"
            f"{moc_desc}\n\n"
            f"## {moc_list}\n",
            encoding="utf-8",
        )

    lines = moc.read_text(encoding="utf-8").splitlines()
    entry = f"- [[{note_base}]] — {title}"
    if not any(note_base in l for l in lines):
        idx = None
        for i, l in enumerate(lines):
            if l.strip() in (f"## {moc_list}", "## Anrufe", "## Calls"):
                idx = i + 1
                break
        if idx is None:
            lines.append(f"## {moc_list}")
            idx = len(lines)
        lines.insert(idx, entry)
    lines = [f"updated: {date_part}" if l.startswith("updated:") else l for l in lines]
    moc.write_text("\n".join(lines) + "\n", encoding="utf-8")


# ============================================================================
# Sprecher-Vorschlaege ("ZUORDNUNG:/MAPPING: Sprecher 1=Stefan; ...") extrahieren
# ============================================================================

def extract_suggestions(summary_path: Path) -> str:
    text = summary_path.read_text(encoding="utf-8")
    lines = text.splitlines()
    suggestion = ""
    for l in lines:
        m = re.match(r"^(ZUORDNUNG|MAPPING):\s*(.*)", l)
        if m:
            suggestion = m.group(2)
            break
    kept = [l for l in lines if not re.match(r"^(ZUORDNUNG|MAPPING):", l)]
    summary_path.write_text("\n".join(kept) + ("\n" if kept else ""), encoding="utf-8")
    return suggestion


# ============================================================================
# Sprecher-Schnipsel (m4a via ffmpeg) + pending.json fuer die Tray-UI
# ============================================================================

def build_speaker_clips(
    dia_json: Path, system_wav: Path, review_dir: Path, pending_path: Path,
    note: Path, app: str, stamp: str, suggestions: str, participants: str, prefix: str,
) -> int:
    dia = json.load(open(dia_json, encoding="utf-8"))
    segs = dia.get("segments", [])
    if not segs:
        return 0

    first: dict[int, float] = {}
    for s in segs:
        first.setdefault(s["speaker"], s["start"])
    order = {spk: i + 1 for i, (spk, _) in enumerate(sorted(first.items(), key=lambda kv: kv[1]))}

    # Claude-Vorschlaege parsen: "Sprecher 1=Stefan; Sprecher 2=?"
    sugg: dict[str, str] = {}
    for part in (suggestions or "").split(";"):
        if "=" in part:
            k, v = part.split("=", 1)
            v = v.strip()
            if v and v != "?":
                sugg[k.strip()] = v

    review_dir.mkdir(parents=True, exist_ok=True)
    speakers = []
    for spk, num in sorted(order.items(), key=lambda kv: kv[1]):
        mine = [s for s in segs if s["speaker"] == spk]
        longest = max(mine, key=lambda s: s["end"] - s["start"])
        start = longest["start"]
        dur = max(min(longest["end"] - start, 8.0), 1.5)
        clip = review_dir / f"speaker_{num}.m4a"
        run([
            ffmpeg_bin(), "-hide_banner", "-loglevel", "error", "-y",
            "-ss", str(start), "-t", str(dur), "-i", str(system_wav),
            "-ar", "44100", "-ac", "1", "-c:a", "aac", "-b:a", "80k", str(clip),
        ])
        label = f"{prefix} {num}"
        speakers.append({
            "label": label,
            "clip": str(clip),
            "suggestion": sugg.get(label, ""),
            "totalSec": round(sum(s["end"] - s["start"] for s in mine), 1),
        })

    names = [n.strip() for n in (participants or "").split(",") if n.strip()]
    pending_path.parent.mkdir(parents=True, exist_ok=True)
    json.dump(
        {"stamp": stamp, "app": app, "note": str(note), "speakers": speakers, "participants": names},
        open(pending_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2,
    )
    return len(speakers)


# ============================================================================
# Ablage-Ziele: Nextcloud (WebDAV), Notion. Apple Notes: bewusst kein Windows-Ziel.
# ============================================================================

def deliver_nextcloud(note: Path, cfg: Config, secrets: dict[str, str]) -> None:
    ncu, ncn, ncp = cfg.nc_url, cfg.nc_user, cfg.nc_pass
    if not ncu:
        ncu = secrets.get("NEXTCLOUD_URL", "")
        ncn = secrets.get("NEXTCLOUD_USER", "")
        ncp = secrets.get("NEXTCLOUD_APPPASS", "")
    if not (ncu and ncn and ncp):
        say("Nextcloud aktiviert, aber URL/Login fehlen (Einstellungen)")
        return

    nc_base = ncu.rstrip("/")
    if "/remote.php/dav" not in nc_base:
        nc_base = f"{nc_base}/remote.php/dav/files/{ncn}"

    import base64
    auth_header = "Basic " + base64.b64encode(f"{ncn}:{ncp}".encode()).decode()

    try:
        req = urllib.request.Request(f"{nc_base}/CallNotes", method="MKCOL",
                                      headers={"Authorization": auth_header})
        urllib.request.urlopen(req, timeout=30).read()
    except Exception:
        pass  # existiert vermutlich schon, wie im Original (>/dev/null 2>&1)

    try:
        data = note.read_bytes()
        req = urllib.request.Request(
            f"{nc_base}/CallNotes/{note.name}", data=data, method="PUT",
            headers={"Authorization": auth_header},
        )
        urllib.request.urlopen(req, timeout=60).read()
        say("Nextcloud: abgelegt")
    except Exception:
        say("Nextcloud fehlgeschlagen (URL/Login pruefen, nicht kritisch)")


def deliver_notion(note: Path, title: str, cfg: Config) -> None:
    raw = re.sub(r"[^0-9a-fA-F]", "", cfg.notion_parent)
    if len(raw) != 32:
        say("Notion fehlgeschlagen (Token/Seiten-ID + Freigabe der Seite fuer die Integration pruefen)")
        return
    pid = f"{raw[0:8]}-{raw[8:12]}-{raw[12:16]}-{raw[16:20]}-{raw[20:32]}"
    body = note.read_text(encoding="utf-8").split("---", 2)[-1].strip()

    blocks = []
    for para in body.split("\n"):
        if not para.strip():
            continue
        blocks.append({
            "object": "block", "type": "paragraph",
            "paragraph": {"rich_text": [{"type": "text", "text": {"content": para[:1900]}}]},
        })
        if len(blocks) >= 95:
            blocks.append({
                "object": "block", "type": "paragraph",
                "paragraph": {"rich_text": [{"type": "text",
                              "text": {"content": "… (gekuerzt, Volltext in CallNotes)"}}]},
            })
            break

    payload = {
        "parent": {"page_id": pid},
        "properties": {"title": {"title": [{"type": "text", "text": {"content": title}}]}},
        "children": blocks,
    }
    try:
        req = urllib.request.Request(
            "https://api.notion.com/v1/pages",
            data=json.dumps(payload).encode(),
            headers={
                "Authorization": f"Bearer {cfg.notion_token}",
                "Notion-Version": "2022-06-28",
                "Content-Type": "application/json",
                "User-Agent": "CallNotes",
            },
        )
        urllib.request.urlopen(req, timeout=30).read()
        say("Notion: abgelegt")
    except Exception:
        say("Notion fehlgeschlagen (Token/Seiten-ID + Freigabe der Seite fuer die Integration pruefen)")


# ============================================================================
# Spiegel-Sync anstossen (callnotes_sync.py)
# ============================================================================

def run_mirror_sync(cfg_path: Path) -> None:
    sync_script = SCRIPT_DIR / "callnotes_sync.py"
    try:
        r = subprocess.run(
            [sys.executable, str(sync_script)],
            env={**os.environ, "CALLNOTES_CONFIG": str(cfg_path)},
            capture_output=True, text=True,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
        for line in (r.stdout or "").splitlines():
            say(f"  {line}")
        for line in (r.stderr or "").splitlines():
            say(f"  {line}")
    except OSError as e:
        say(f"  Spiegel-Sync konnte nicht gestartet werden: {e}")


# ============================================================================
# Hauptablauf
# ============================================================================

def main() -> int:
    if len(sys.argv) < 2:
        say("Nutzung: process_call.py <aufnahme-verzeichnis>")
        return 1

    rec = Path(sys.argv[1].rstrip("\\/"))
    cfg_path = Path(expand(os.environ.get("CALLNOTES_CONFIG"), r"%APPDATA%\callnotes\config.json"))

    if not cfg_path.is_file():
        say(f"FEHLER: Config fehlt: {cfg_path} (install.ps1 ausfuehren)")
        return 1

    cfg = Config(cfg_path)
    texts = Texts(cfg)

    secrets_path = Path(expand(r"%APPDATA%\callnotes\secrets.env"))
    if not secrets_path.is_file():
        secrets_path = Path(expand(r"%APPDATA%\dasgeht\secrets.env"))
    secrets = load_secrets_env(secrets_path)

    groq_key = cfg.groq_api_key or secrets.get("GROQ_API_KEY") or secrets.get("FABLE_GROQ_KEY") or ""

    if not rec.is_dir():
        say(f"FEHLER: Verzeichnis fehlt: {rec}")
        return 1

    if not (cfg.transcriber == "groq" and groq_key):
        if not (cfg.model and Path(cfg.model).is_file()):
            say(f"FEHLER: Whisper-Modell fehlt (config 'whisperModel'): {cfg.model or 'nicht gesetzt'}")
            return 1

    lock = ProcessLock(cfg.base)
    lock.acquire()

    # STAMP aus Verzeichnisname ableiten (yyyy-MM-dd_HHmmss oder aeltere yyyy-MM-dd_HHmm)
    dirname = rec.name
    m = re.match(r"^(\d{4}-\d{2}-\d{2}_\d{4,6})", dirname)
    stamp = m.group(1) if m else dirname[:15]
    date_part = stamp[0:10] if len(stamp) >= 10 else datetime.now().strftime("%Y-%m-%d")
    time_part = stamp[11:15] if len(stamp) >= 15 else datetime.now().strftime("%H%M")
    if len(date_part) != 10:
        date_part = datetime.now().strftime("%Y-%m-%d")
    if len(time_part) != 4:
        time_part = datetime.now().strftime("%H%M")
    time_nice = f"{time_part[0:2]}:{time_part[2:4]}"

    state = ProcessingState(cfg.base, stamp)

    try:
        say(f"Verarbeite {rec}")

        # --- Meta lesen ---------------------------------------------------
        meta_path = rec / "meta.json"
        app = "unbekannt"
        dur = 0
        if meta_path.is_file():
            try:
                meta = json.load(open(meta_path, encoding="utf-8"))
                app = meta.get("appName", "unbekannt")
                dur = meta.get("durationSec", 0)
            except (OSError, json.JSONDecodeError):
                pass
        dur_min = (dur + 59) // 60

        # --- Transkription -------------------------------------------------
        state.phase(texts.ph_trans)
        say(f"Transkribiere ({cfg.transcriber}, {cfg.language}) …")
        mic_ok = transcribe_track(rec, "mic.wav", "mic", False, cfg, groq_key)
        sys_ok = transcribe_track(rec, "system.wav", "system", True, cfg, groq_key)
        if not mic_ok and not sys_ok:
            fail("beide Spuren leer/nicht transkribierbar", cfg.base, rec, state, cfg)

        # --- Diarisierung ----------------------------------------------------
        n_speakers = 1
        dia_json = rec / "diarization.json"
        if sys_ok:
            n_speakers = run_diarization(rec, cfg, texts, state)
        else:
            (rec / "system.16k.wav").unlink(missing_ok=True)

        # --- Dialog mergen -----------------------------------------------------
        dialog_path = rec / "dialog.md"
        merge_script = SCRIPT_DIR / "merge_transcript.py"
        merge_args = [sys.executable, str(merge_script), str(rec / "mic.json"), str(rec / "system.json"),
                      texts.self_label, texts.peer_label]
        if n_speakers > 1:
            merge_args += [str(dia_json), texts.speaker_prefix]
        r = run(merge_args, text=True)
        dialog_path.write_text(r.stdout or "", encoding="utf-8")
        if not (dialog_path.is_file() and dialog_path.stat().st_size > 0):
            fail("Dialog-Merge leer", cfg.base, rec, state, cfg)

        dialog = dialog_path.read_text(encoding="utf-8")
        word_count = len(dialog.split())
        say(f"Transkript: {word_count} Woerter")

        # Teilnehmer-Namen aus dem Live-Popup
        participants = ""
        participants_path = rec / "participants.json"
        if participants_path.is_file():
            try:
                names = json.load(open(participants_path, encoding="utf-8")).get("names", [])
                participants = ", ".join(names)
            except (OSError, json.JSONDecodeError):
                pass

        # --- KI-Zusammenfassung ------------------------------------------------
        state.phase(texts.ph_ai)
        say(f"Zusammenfassung ({cfg.summarizer}) …")
        summary_text = make_summary(dialog, cfg, texts, n_speakers, participants, rec)
        summary_path = rec / "summary.md"
        if summary_text is None:
            if cfg.summarizer == "off":
                say("  KI-Zusammenfassung deaktiviert — Notiz mit Transkript")
            else:
                say("  KI nicht verfuegbar — Notiz ohne Zusammenfassung")
            if texts.de:
                summary_text = (
                    f"# Telefonat via {app} — {date_part} {time_nice}\n\n"
                    "## Kurzfassung\n"
                    "_Automatische Zusammenfassung nicht verfuegbar — Transkript unten._"
                )
            else:
                summary_text = (
                    f"# Call via {app} — {date_part} {time_nice}\n\n"
                    "## Summary\n"
                    "_Automatic summary unavailable — transcript below._"
                )
        summary_path.write_text(summary_text, encoding="utf-8")

        # KI-Namensvorschlaege extrahieren + aus Notiz strippen
        suggestions = extract_suggestions(summary_path)

        # --- Notiz bauen -----------------------------------------------------
        cfg.notes_dir.mkdir(parents=True, exist_ok=True)
        summary_lines = summary_path.read_text(encoding="utf-8").splitlines()
        title = re.sub(r"^# *", "", summary_lines[0]) if summary_lines else app
        slug = slugify(title) or app
        note = build_note_path(cfg.notes_dir, date_part, time_part, slug)
        m4a_name = f"{stamp}_{app}.m4a"

        note_body = []
        note_body.append("---")
        note_body.append("type: Note")
        note_body.append("tags: [call, telefonat, log]")
        note_body.append(f"app: {app}")
        note_body.append(f"dauer: {dur_min}min")
        note_body.append(f"updated: {date_part}")
        note_body.append("---")
        note_body.append("")
        note_body.append(summary_path.read_text(encoding="utf-8"))
        note_body.append("")
        note_body.append(f"## {texts.transcript_hdr}")
        note_body.append("")
        note_body.append(dialog)
        note_body.append("")
        note_body.append("---")
        note_body.append(
            f"{texts.audio_note}: `{cfg.audio_dir / m4a_name}` "
            f"({texts.left} = {texts.self_label}, {texts.right} = {texts.peer_label})"
        )
        if cfg.moc_on:
            note_body.append("")
            note_body.append("[[anrufe-moc]]")
        note.write_text("\n".join(note_body) + "\n", encoding="utf-8")

        if cfg.moc_on:
            moc = cfg.notes_dir / "anrufe-moc.md"
            update_moc(moc, note.stem, title, date_part, texts)

        say(f"Notiz: {note}")

        state.phase(texts.ph_store)

        # --- Audio-Archiv (Stereo-m4a: L=selbst, R=Gegenseite) ------------------
        cfg.audio_dir.mkdir(parents=True, exist_ok=True)
        m4a = cfg.audio_dir / m4a_name
        mic_src = rec / "mic.wav"
        sys_src = rec / "system.wav"
        mic_has = mic_src.is_file() and mic_src.stat().st_size > 0
        sys_has = sys_src.is_file() and sys_src.stat().st_size > 0
        if mic_has and sys_has:
            run([
                ffmpeg_bin(), "-hide_banner", "-loglevel", "error", "-y",
                "-i", str(mic_src), "-i", str(sys_src),
                "-filter_complex",
                "[0:a]aresample=48000,pan=mono|c0=c0[l];"
                "[1:a]aresample=48000,pan=mono|c0=c0[r];"
                "[l][r]join=inputs=2:channel_layout=stereo[a]",
                "-map", "[a]", "-c:a", "aac", "-b:a", "96k", str(m4a),
            ])
        elif mic_has or sys_has:
            src = mic_src if mic_has else sys_src
            run([
                ffmpeg_bin(), "-hide_banner", "-loglevel", "error", "-y",
                "-i", str(src), "-ar", "48000", "-c:a", "aac", "-b:a", "96k", str(m4a),
            ])

        # --- Mehrere Sprecher: Hoer-Schnipsel + Zuordnungs-Auftrag -----------------
        if n_speakers > 1 and dia_json.is_file() and dia_json.stat().st_size > 0 and sys_has:
            review_dir = cfg.base / "review" / stamp
            pending_dir = cfg.base / "state" / "pending"
            count = build_speaker_clips(
                dia_json, sys_src, review_dir, pending_dir / f"{stamp}.json",
                note, app, stamp, suggestions, participants, texts.speaker_prefix,
            )
            say(f"Sprecher-Schnipsel + Zuordnungs-Auftrag erstellt ({count} Sprecher, CallNotes-Tray)")

        # --- Ablage-Ziele -------------------------------------------------------
        if cfg.dest_apple_notes:
            # Kein Windows-Aequivalent (contract.md Abschnitt 10) — Flag wird
            # geparst, aber ignoriert; einmaliger Hinweis statt Fehler.
            say("Apple Notes ist auf Windows nicht verfuegbar — Ziel wird ignoriert (siehe contract.md)")

        if cfg.dest_nextcloud:
            deliver_nextcloud(note, cfg, secrets)

        if cfg.dest_notion and cfg.notion_token and cfg.notion_parent:
            deliver_notion(note, title, cfg)

        # --- Spiegel-Kopie (z.B. externe Platte), holt auch Verpasstes nach --------
        run_mirror_sync(cfg_path)

        # --- Aufraeumen + Push ---------------------------------------------------
        if m4a.is_file() and m4a.stat().st_size > 0:
            shutil.rmtree(rec, ignore_errors=True)
        else:
            say(f"m4a fehlgeschlagen — Rohaufnahme bleibt in {rec}")

        extra_push = ""
        if n_speakers > 1:
            extra_push = f" — {n_speakers} Sprecher, Zuordnung im CallNotes-Tray"
        ntfy(
            f"📞 {title} ({dur_min}min via {app}) → {note.name}{extra_push}",
            "Anruf-Notiz",
            cfg.ntfy_url,
        )
        state.done()
        say(f"FERTIG: {title}")
        return 0

    except ProcessingFailed:
        return 1
    finally:
        lock.release()


if __name__ == "__main__":
    sys.exit(main())

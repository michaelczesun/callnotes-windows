#!/usr/bin/env python3
# callnotes_sync.py — spiegelt Notizen + Audio-Archiv in den Kopie-Ordner
# (z.B. externe Festplatte/USB). Holt automatisch alles nach, was beim
# letzten Mal gefehlt hat (Platte nicht angeschlossen o.ae.).
# Windows-Port von callnotes/callnotes-sync.sh: robocopy statt rsync (beides
# inkrementell + idempotent bei wiederholtem Lauf; robocopy /MIR waere zu
# aggressiv — wir wollen wie rsync -a nur HINZUFUEGEN/aktualisieren, nichts im
# Ziel loeschen, was in der Quelle fehlt, daher robocopy OHNE /MIR, /XX-Flags).
# Falls robocopy fehlt (z.B. minimaler Windows-Sandbox-Build), Fallback auf
# shutil.copy2 mit manueller Mtime/Size-Vergleich-Logik.
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path


def expand(p: str | None, default: str = "") -> str:
    raw = p or default
    if not raw:
        return ""
    return os.path.expandvars(raw)


def load_config() -> tuple[Path, Path, str]:
    cfg_path = Path(expand(os.environ.get("CALLNOTES_CONFIG"), r"%APPDATA%\callnotes\config.json"))
    if not cfg_path.is_file():
        print(f"Config fehlt: {cfg_path}", file=sys.stderr)
        sys.exit(1)
    d = json.load(open(cfg_path, encoding="utf-8"))
    notes_dir = Path(expand(d.get("notesDir"), r"%USERPROFILE%\CallNotes\notes"))
    audio_dir = Path(expand(d.get("audioDir"), r"%USERPROFILE%\CallNotes\audio"))
    mirror_dir = expand(d.get("mirrorDir"))
    return notes_dir, audio_dir, mirror_dir


def robocopy_bin() -> str | None:
    return shutil.which("robocopy")


def sync_dir_robocopy(src: Path, dst: Path) -> bool:
    """robocopy Quelle -> Ziel, nur neue/geaenderte Dateien (wie rsync -a --exclude '.*').
    Exit-Codes 0-7 sind bei robocopy ALLE Erfolg (Bitmask: 1=kopiert, 2=zusaetzlich,
    4=nicht kopierbar/Mismatch, 8+=Fehler) — erst ab 8 ist es ein echter Fehler."""
    cmd = [
        robocopy_bin(), str(src), str(dst),
        "/E",           # inkl. leerer Unterordner, rekursiv
        "/XO",          # aeltere Dateien im Ziel nicht ueberschreiben (rsync -a Analog: neuere gewinnen)
        "/XF", ".*",    # versteckte/Punkt-Dateien wie rsync --exclude ".*"
        "/R:2", "/W:2", # wenige Retries, kurze Wartezeit (kein Endlos-Haengen bei Netzlaufwerken)
        "/NFL", "/NDL", "/NJH", "/NJS", "/NP",  # ruhige Ausgabe
    ]
    r = subprocess.run(
        cmd, capture_output=True, text=True,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    return r.returncode < 8


def sync_dir_fallback(src: Path, dst: Path) -> bool:
    """Fallback ohne robocopy: manuelles rekursives Kopieren, nur neuere/fehlende
    Dateien, Punkt-Dateien ausgeschlossen — funktional aequivalent zu rsync -a."""
    ok = True
    try:
        for root, dirs, files in os.walk(src):
            dirs[:] = [d for d in dirs if not d.startswith(".")]
            rel = Path(root).relative_to(src)
            target_root = dst / rel
            target_root.mkdir(parents=True, exist_ok=True)
            for fname in files:
                if fname.startswith("."):
                    continue
                s = Path(root) / fname
                t = target_root / fname
                try:
                    if t.exists() and t.stat().st_mtime >= s.stat().st_mtime and t.stat().st_size == s.stat().st_size:
                        continue
                    shutil.copy2(s, t)
                except OSError:
                    ok = False
    except OSError:
        ok = False
    return ok


def sync_dir(src: Path, dst: Path) -> bool:
    if not src.is_dir():
        return True  # nichts zu tun, wie im Original "[ -d ... ] &&"
    if robocopy_bin():
        return sync_dir_robocopy(src, dst)
    return sync_dir_fallback(src, dst)


def main() -> int:
    notes_dir, audio_dir, mirror_dir = load_config()

    if not mirror_dir:
        print("Kein Kopie-Ordner konfiguriert (mirrorDir) — nichts zu tun.")
        return 0

    mirror_path = Path(mirror_dir)

    # Nicht angeschlossenes externes Laufwerk erkennen: das uebergeordnete
    # Verzeichnis (bzw. Laufwerksbuchstabe) muss existieren.
    parent = mirror_path.parent
    if not parent.is_dir() and not mirror_path.is_dir():
        print(f"Kopie-Ziel nicht erreichbar (Platte nicht angeschlossen?): {mirror_path} — uebersprungen.")
        return 0

    notizen_dst = mirror_path / "notizen"
    audio_dst = mirror_path / "audio"
    try:
        notizen_dst.mkdir(parents=True, exist_ok=True)
        audio_dst.mkdir(parents=True, exist_ok=True)
    except OSError:
        print(f"Kopie-Ziel nicht beschreibbar: {mirror_path}", file=sys.stderr)
        return 1

    ok = True
    ok = sync_dir(notes_dir, notizen_dst) and ok
    ok = sync_dir(audio_dir, audio_dst) and ok

    if ok:
        print(f"Spiegel aktuell: {mirror_path} (notizen/ + audio/)")
        return 0
    else:
        print(f"Spiegel unvollstaendig (Zugriff verweigert? Laufwerk pruefen): {mirror_path}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

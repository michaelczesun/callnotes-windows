#!/usr/bin/env python3
# apply_speakers.py — uebernimmt die Sprecher-Zuordnung in die fertige Notiz.
# Nutzung: apply_speakers.py <pending.json> "Sprecher 1=Stefan;Sprecher 2=Anna"
# Leere Zuordnung oder "?" laesst das Label unveraendert. Danach: pending +
# Schnipsel aufraeumen und Spiegel-Sync anstossen.
# Windows-Port von callnotes/apply-speakers.sh — Logik 1:1 identisch, Bash-Wrapper
# entfaellt (direkt in Python, vom CallTap.Tray SpeakerAssignView.xaml.cs aufgerufen
# oder manuell: python apply_speakers.py <pending.json> "<mapping>").
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent


def main() -> int:
    if len(sys.argv) < 2:
        print('Nutzung: apply_speakers.py <pending.json> "Sprecher 1=Name;..."', file=sys.stderr)
        return 1

    pending_p = Path(sys.argv[1])
    mapping = sys.argv[2] if len(sys.argv) > 2 else ""

    if not pending_p.is_file():
        print(f"pending.json fehlt: {pending_p}", file=sys.stderr)
        return 1

    d = json.load(open(pending_p, encoding="utf-8"))
    note = Path(d.get("note", ""))
    if not note.exists():
        print(f"Notiz fehlt: {note}", file=sys.stderr)
        return 1

    # robust gegen Semikolons in Namen: Segmente laufen bis zum naechsten
    # "Sprecher/Speaker N="
    pairs = []
    pattern = re.compile(
        r"((?:Sprecher|Speaker) \d+)\s*=\s*([^;]*(?:;(?!\s*(?:Sprecher|Speaker) \d+\s*=)[^;]*)*)"
    )
    for m in pattern.finditer(mapping or ""):
        k, v = m.group(1).strip(), m.group(2).strip().rstrip(";").strip()
        if k and v and v != "?" and v != k:
            pairs.append((k, v))

    if pairs:
        text = note.read_text(encoding="utf-8")
        # laengere Labels zuerst ersetzen (Sprecher 10 vor Sprecher 1)
        for k, v in sorted(pairs, key=lambda kv: -len(kv[0])):
            text = text.replace(k, v)
        note.write_text(text, encoding="utf-8")
        applied = "; ".join(f"{k} -> {v}" for k, v in pairs)
        print(f"Notiz aktualisiert: {note.name} ({applied})")
    else:
        print("Keine Zuordnung uebernommen (Labels bleiben).")

    # Schnipsel + Auftrag aufraeumen
    for s in d.get("speakers", []):
        clip = s.get("clip", "")
        if clip and Path(clip).exists():
            shutil.rmtree(Path(clip).parent, ignore_errors=True)
            break
    try:
        pending_p.unlink()
    except OSError:
        pass

    # Spiegel-Sync anstossen (best-effort, wie im Original "|| true")
    sync_script = SCRIPT_DIR / "callnotes_sync.py"
    try:
        subprocess.run(
            [sys.executable, str(sync_script)],
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
    except OSError:
        pass

    return 0


if __name__ == "__main__":
    sys.exit(main())

<p align="center">
  <a href="README.md">🇬🇧 English</a>&nbsp;&nbsp;·&nbsp;&nbsp;<b>🇩🇪 Deutsch</b>
</p>

<h1 align="center">CallNotes für Windows</h1>

<p align="center">
  ⚠️ <b>Experimentell</b> — code-vollständiges Schwesterprojekt von
  <a href="https://github.com/michaelczesun/callnotes">CallNotes für macOS</a>;
  in CI auf Windows kompiliert, sucht Tester — bitte den ersten Testlauf als
  Issue melden!
</p>

<p align="center">
  Du telefonierst am Windows-PC — CallNotes nimmt <b>beide Seiten als getrennte
  Spuren</b> auf, transkribiert lokal mit Whisper, trennt die Sprecher und legt
  dir eine fertige, KI-zusammengefasste Notiz ab, wo du willst. Vollautomatisch,
  aus dem Tray-Icon.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%2020348%2B%20%2F%2011-black?logo=windows" alt="Windows 10 Build 20348+ / 11">
  <img src="https://img.shields.io/badge/Transkription-on--device-6D5CFF" alt="on-device">
  <img src="https://img.shields.io/badge/Lizenz-PolyForm%20Noncommercial-BF5AF2" alt="Lizenz">
</p>

---

## Das ist der Windows-Port

Dieses Projekt ist eine von Grund auf neu geschriebene C#-Umsetzung von
[**CallNotes für macOS**](https://github.com/michaelczesun/callnotes) — **dem
Original** — und bleibt bewusst im Gleichschritt damit: gleiche Config-Keys,
gleiche State-File-Formen, gleiche Verarbeitungs-Pipeline (whisper.cpp,
sherpa-onnx, KI deiner Wahl). Wer einen Mac hat, nutzt am besten jenes Projekt —
es ist die gereifte, täglich genutzte Version. Dieses Repo existiert, damit
Windows-Nutzer dasselbe Tool bekommen, gebaut auf dem Windows-nativen Äquivalent
der Mac-Aufnahmetechnik.

**Warum es einen eigenen Recorder-Kern braucht statt eines reinen Ports:** Die
Mac-App nimmt Systemaudio über Core-Audio-*Process-Taps* auf (macOS 14.2+), eine
reine macOS-API ohne Windows-Pendant. Windows hat seit **Windows 10 Build 20348**
eine andere, aber konzeptionell parallele Fähigkeit: **WASAPI Process Loopback
Capture** (`ActivateAudioInterfaceAsync` +
`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`). Sie erfasst nur das Audio, das
*ein einzelner Prozess* (+ sein Kindprozessbaum) ausgibt — kein virtueller
Audio-Treiber, kein systemweiter Loopback, kein Stereo-Mix-Hack. Das ist das
Windows-native Gegenstück zum Process Tap der Mac-App, und der Grund, warum
dieses Projekt einen handgeschriebenen C#-Recorder-Kern ("CallTap") hat statt
NAudios eingebauten Loopback zu nutzen (der deckt nur den klassischen
Full-Endpoint-Loopback ab, nicht Prozess-spezifisch). Alles Nachgelagerte — die
Python-Verarbeitungs-Pipeline, das Config-Schema, die State-Files, das
Notiz-Format — wird unverändert aus dem Mac-Projekt übernommen.

## Warum es das gibt

Jedes Call-Recording-Tool braucht entweder einen virtuellen Audio-Treiber, einen
sichtbaren Meeting-Bot oder ein Cloud-Abo. CallNotes nicht:

- **WASAPI Process Loopback Capture** greift das Systemaudio **nur der Call-App**
  ab (+ ihren Prozessbaum) — die Gegenseite landet auf ihrer eigenen Spur,
  Hintergrundmusik nicht.
- Dein Mikrofon läuft parallel — **zwei getrennte Spuren bedeuten perfekte
  Sprecher-Zuordnung bei 1:1-Anrufen**, ganz ohne KI-Raterei.
- Bei Konferenzen trennt eine lokale **Sprecher-Diarisierung** (sherpa-onnx) den
  Mix der Gegenseite in „Sprecher 1..N" — Namen ordnest du per Hör-Schnipsel und
  Dropdown zu.
- Transkription läuft **on-device** (whisper.cpp) — oder über die Groq-API, wenn
  dir Tempo wichtiger ist als volle Offline-Nutzung. Ein Config-Schalter.
- **Parakeet TDT v3** (sherpa-onnx, on-device) ist die schnellste lokale Option,
  mit Unterstützung für 25 EU-Sprachen — laden via `install.ps1 -WithParakeet`.
- **KI deiner Wahl** für die Zusammenfassung: Claude Code (Standard), jede
  OpenAI-kompatible API (OpenAI, Groq, OpenRouter), komplett lokal via **Ollama**
  — oder ganz ohne.

## Was du nach dem Auflegen bekommst

Eine fertige Markdown-Notiz, etwa eine Minute später:

- **Kurzfassung, besprochene Punkte, Zusagen & To-dos, offene Fragen** (optional
  — Sektionen frei wählbar, inklusive Follow-up-Mail-Entwurf)
- **Dialog-Transkript mit Sprechern** („Ich: … / Gesprächspartner: …") mit
  Zeitstempeln
- **Audio-Archiv** (`mic.wav` + `system.wav`)
- Abgelegt im **Notizen-Ordner** (Obsidian-tauglich), optional zusätzlich in
  **Nextcloud, Notion**, plus **ntfy-Push**

## Die Tray-App

Alles sitzt im System-Tray (Telefon-Symbol):

- **Live-Ansicht im Anruf** — Pegel-Anzeigen für Mikrofon + Systemaudio,
  Anruf-Timer, und ein Popup zum Eintragen der Teilnehmer-Namen, solange du sie
  noch im Kopf hast
- **Verarbeitungs-Status** nach dem Auflegen (Transkription → Sprecher-Erkennung
  → KI-Zusammenfassung)
- **Sprecher-Zuordnung** bei Konferenzen: Hör-Schnipsel je erkannter Stimme
  abspielen, Name im Dropdown wählen
- **Letzte Anrufe**, Speicherorte, API-Keys, Integrationen
- **Ersteinrichtungs-Assistent** und eine Einstellungen-UI mit ⓘ neben jedem
  Feld, analog zur Mac-App
- **Deutsch & Englisch** — die App folgt automatisch deiner Systemsprache
  (`uiLanguage: "system" | "de" | "en"`)

`CallTap.Cli` (das eigenständige `calltap.exe`) funktioniert auch ganz ohne
Tray-App, für Skripte oder einen Scheduled-Task-Betrieb.

## Was v1 kann — und was (noch) fehlt

**Funktioniert heute — erster Feldtest auf echtem Windows bestanden (11 ARM64
24H2, 3.7.2026): `calltap procs` läuft sauber, und die Zwei-Spuren-Aufnahme ist
bewiesen — ein 440-Hz-Testton via Process Loopback mit −0,1 dBFS Peak
aufgenommen, Mikrofon parallel erfasst. Nativer ARM64-Build funktioniert.
Weiterhin experimentell — mehr Maschinen, mehr Berichte, bitte:**

- Pro-App-Systemaudio-Aufnahme via WASAPI Process Loopback (inkl./exkl.
  Kindprozessbaum der Ziel-App)
- Parallele Mikrofon-Aufnahme (NAudio `WasapiCapture`)
- Die komplette Watch-Loop-Zustandsmaschine, Zeile für Zeile aus der Mac-App
  (`calltap.swift`) portiert (`minSeconds`/`stopGraceSeconds`/`maxHours`,
  Suppress-nach-Verwerfen, Abort-Datei-Protokoll, Aufräumen verwaister
  Aufnahmen beim Start)
- Dieselbe Python-Verarbeitungs-Pipeline: **whisper.cpp / Parakeet TDT v3 / Groq**-
  Transkription, sherpa-onnx-Diarisierung, Transkript-Merge, KI-Zusammenfassung,
  Markdown-Notiz + MOC-Pflege, Nextcloud-/Notion-Zustellung
- `calltap procs [--watch]` / `calltap setup` / `calltap record` / `calltap
  watch` — dieselbe CLI-Form wie das Mac-`calltap`

**Was fehlt oder bewusst Windows-spezifisch ist — keine Versäumnisse:**

- **Keine iPhone-/Mobilfunk-Anrufe.** Die Continuity-basierte
  Telefonanruf-Erfassung der Mac-App hat auf Windows kein Pendant — das ist eine
  harte Plattformgrenze, kein offener Punkt. Die `apps`-Allowlist von v1 deckt
  nur **Desktop-VoIP-Apps** ab: WhatsApp, Zoom, Teams, Discord.
- **Kein Apple-Notes-Pendant.** Es gibt keine Windows-Entsprechung zur
  Automatisierungsfläche von Apple Notes. `destinations.appleNotes` wird
  eingelesen und stillschweigend ignoriert (mit Log-Warnung), damit
  `config.json` zwischen beiden Schwesterprojekten portabel bleibt; als
  zusätzliche Zustellwege sind auf Windows nur **Nextcloud** und **Notion**
  implementiert.
- **Signal Desktop / Telegram Desktop** stehen in der Beispiel-`apps`-Liste der
  Mac-App, sind aber bewusst nicht in der Windows-v1-Zielliste — das
  nachzurüsten ist eine Ein-Zeilen-Config-Änderung (`ProcessMatcher` ist generisch
  über Exe-Namen), nur eben noch nicht verifiziert.
- **Kein MSIX/Store-Paket.** Ausgeliefert wird ein signiertes portables EXE +
  PowerShell-Installer, dieselbe „klonen und installieren"-Philosophie wie das
  `install.sh` der Mac-App.
- **Vorerst nur win-x64** — `net8.0-windows` und NAudio unterstützen zwar auch
  `win-arm64`, aber es gibt noch keine ARM64-Windows-Testabdeckung.
- **Bisher in einer VM getestet, nicht auf echter Hardware.** Der Feldtest oben
  lief in einer Windows-11-ARM64-VM (UTM/QEMU auf Apple Silicon): Build, `procs`,
  `setup` und eine echte Loopback-Aufnahme laufen dort durch. Echte Hardware,
  x64-Maschinen und echte VoIP-Anrufe sind genau das, was noch ungetestet ist —
  Berichte in den Issues sind der wertvollste Beitrag gerade.

## Läuft lokal — was auf deinem Rechner landet

Alles wird auf deinem PC verarbeitet: .NET 8 + Git (Build), Python (Pipeline),
whisper.cpp (`whisper-cli.exe` + ein ggml-Modell — bei ≤ 8 GB RAM `ggml-small`
statt large wählen) und optional sherpa-onnx für Sprechertrennung/Parakeet.
`installer/install.ps1` richtet alles ein; Cloud ist nirgends Pflicht (Groq und
die KI-Zusammenfassung sind opt-in). Du installierst/debuggst mit einem
KI-Assistenten? Gib ihm **[CLAUDE.md](CLAUDE.md)** — dort stehen die lokalen
Abhängigkeiten und die im Feldtest gelernten Fallstricke.

## Installation

Setzt **Windows 10 Build 20348+ oder Windows 11** voraus (ältere Windows-10-Builds
unterstützen die Process-Loopback-Aktivierung nicht — `calltap setup` sagt dir
klar, wenn dein Build zu alt ist).

```powershell
git clone https://github.com/michaelczesun/callnotes-windows
cd callnotes-windows
./installer/install.ps1
```

`install.ps1` installiert Abhängigkeiten (ffmpeg, Python) über winget/choco,
richtet ein Python-venv mit den Pipeline-Requirements ein, legt ein
Standard-`%APPDATA%\callnotes\config.json` an und registriert einen Scheduled
Task / Autostart-Eintrag für die Tray-App. Außerdem führt es dich durch die
Windows-Datenschutzabfrage für Mikrofonzugriff (Einstellungen → Datenschutz &
Sicherheit → Mikrofon → Desktop-Apps erlauben).

Whisper-Modell einmalig laden (~550 MB):

```powershell
mkdir $env:USERPROFILE\models -Force
curl.exe -L -o $env:USERPROFILE\models\ggml-large-v3-turbo-q5_0.bin `
  https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin
```

**Beim ersten Start:** `calltap setup` einmal ausführen — es fordert
Mikrofonzugriff an (löst die Windows-Zustimmungsabfrage aus) und führt einen
harmlosen Selbsttest der Process-Loopback-Aktivierung durch, um zu bestätigen,
dass dein Rechner/OS-Build das tatsächlich unterstützt.

Danach: Testanruf machen (länger als 20 Sekunden). Fortschritt bei Bedarf in
`%USERPROFILE%\CallNotes\log\process.log`.

## Unterstützte Call-Apps

WhatsApp (Desktop), Zoom, Microsoft Teams (neues, WebView2-basiertes), Discord —
alles, was das Mikrofon nutzt und in deiner Allowlist steht. Den Exe-Namen jeder
App findest du mit `calltap procs --watch` während eines Anrufs (unbekannte
aktive Mikrofon-Prozesse landen automatisch im Log, damit du sie ergänzen
kannst).

## Konfiguration

Alles liegt in `%APPDATA%\callnotes\config.json` — dieselben Keys wie bei der
Mac-App, nur dass `apps` **Executable-Namen** statt Bundle-IDs enthält. Die
wichtigsten Felder:

| Feld | Bedeutung |
|---|---|
| `apps` | Executable-Namen, deren Mikrofon-Nutzung eine Aufnahme startet (z. B. `WhatsApp.exe`) |
| `tapScope` | `app` = nur den Prozessbaum der Call-App via Process-Loopback aufnehmen (Default), `global` = klassischer Full-Endpoint-Loopback ohne Prozessfilter |
| `minSeconds` / `stopGraceSeconds` / `maxHours` | Verwerfen-wenn-zu-kurz-Schwelle, Auflege-Entprellung, erzwungene Maximaldauer |
| `transcriber` / `groqApiKey` | `local` (whisper.cpp) oder `groq` (Cloud, schneller) |
| `summarizer` (+ `summarizerUrl/Model/ApiKey`) | `claude` (Claude Code CLI), `openai` (jede OpenAI-kompatible API inkl. Ollama/Groq/OpenRouter) oder `off` |
| `noteSections` | welche Abschnitte geschrieben werden: Kurzfassung, Besprochen, To-dos, Follow-up-Mail |
| `destinations` | zusätzliche Ablage: Nextcloud (WebDAV), Notion (kein Apple Notes unter Windows) |
| `notesDir` / `audioDir` / `mirrorDir` | wohin Notizen, Audio und der Externe-Platte-Spiegel gehen |
| `diarize` / `diarizeThreshold` | Mehrsprecher-Erkennung an/aus, Cluster-Schwelle |
| `speakerSelf` / `speakerPeer` / `context` | dein Name / Label der Gegenseite im Transkript + ein Satz Kontext für bessere Zusammenfassungen |
| `micDeviceId` | *(nur Windows)* NAudio-Aufnahmegerät überschreiben; leer = Standardmikrofon |
| `processLoopbackMode` | *(nur Windows)* `includeTree` (Default) oder `excludeTree` — direkt auf `PROCESS_LOOPBACK_MODE` gemappt |
| `venvPython` | Pfad zum Python-venv-Interpreter der Pipeline |

`%APPDATA%`, `%USERPROFILE%`, `%LOCALAPPDATA%` in `config.json` werden von der App
beim Laden expandiert — es sind reine String-Platzhalter im JSON, keine echten
Umgebungsvariablen-Referenzen.

## CLI

```powershell
calltap.exe procs [--watch]               # welcher Prozess nutzt gerade das Mikrofon?
calltap.exe setup                         # Berechtigungs- + Process-Loopback-Fähigkeitscheck
calltap.exe record --out DIR [--exe NAME] # manuelle Aufnahme (Ctrl-C stoppt)
calltap.exe watch [--config FILE]         # Watch-Daemon im Vordergrund
python pipeline/process_call.py DIR       # eine Aufnahme (nach)verarbeiten
```

## Wenn etwas hakt

- **`calltap setup` scheitert am Process-Loopback-Selbsttest:** dein
  Windows-Build ist älter als Build 20348 (Windows 10 20H1 oder früher) — diese
  API gibt es dort nicht. Windows aktualisieren, oder auf `"tapScope": "global"`
  ausweichen (funktioniert auf jedem WASAPI-fähigen Windows, nimmt dann aber
  *das gesamte* Systemaudio auf, nicht nur die Call-App).
- **System-Spur ist stumm:** Einstellungen → Datenschutz & Sicherheit → Mikrofon
  → Desktop-Apps erlauben prüfen; außerdem checken, ob die Ziel-App im
  Windows-Lautstärkemixer stummgeschaltet ist (Process-Loopback nimmt weiterhin
  Stille auf, wenn die App selbst keinen Ton produziert).
- **Aufnahme startet nicht:** `%USERPROFILE%\CallNotes\log\callwatch.log`
  prüfen — steht dort eine aktive Mikrofon-Session für einen nicht gelisteten
  Prozessnamen, diesen Exe-Namen in `apps` ergänzen.
- **Gegenseite fehlt bei WebView2-/Electron-Apps** (Teams, Discord, WhatsApp):
  Der Ton läuft womöglich in einem Helper-Prozess; `tapScope: "app"`
  (Include-Tree-Modus) sollte die gesamte Prozessfamilie ab der erkannten
  Executable bereits abdecken. Falls trotzdem etwas fehlt, ist das eine der
  offenen Verifikationsfragen des Projekts (siehe `docs/contract.md` §11) —
  bitte als Issue mit App-Version melden.
- **Ausgabegerät mitten im Anruf gewechselt** (z. B. Bluetooth-Headset
  verbunden): Die laufende Aufnahme kann still werden — vor dem Anruf wechseln.
- Fehlgeschlagene Verarbeitungen liegen mit Roh-Audio in
  `%USERPROFILE%\CallNotes\failed\` und lassen sich mit
  `python pipeline/process_call.py <ordner>` erneut anstoßen.

## FAQ

**Ist das schon so ausgereift wie die Mac-Version?**
Noch nicht — genau dafür steht das „Experimentell"-Label. Die macOS-App wird
seit einer Weile täglich vom Autor genutzt; dieser Windows-Port ist
code-vollständig und kompiliert/marshaled COM korrekt in CI, hat aber noch nicht
dieselben echten Anruf-Stunden gesammelt. Wer es testet: bitte ein Issue mit
Windows-Build-Nummer und dem Ergebnis des ersten Anrufs öffnen — das ist gerade
das Wertvollste.

**Warum C# statt eines reinen Code-Ports?**
Der Aufnahme-Kern der Mac-App ist Swift + Core-Audio-Process-Taps, die es unter
Windows schlicht nicht gibt. Das Windows-native Äquivalent (WASAPI Process
Loopback) brauchte eine eigene Interop-Schicht (siehe `docs/contract.md` §1 und
§7 für die vollständige technische Begründung inkl. Code). Alles, was *keine*
plattformspezifische Aufnahme ist — die Python-Verarbeitungs-Pipeline — wird
unverändert übernommen.

**Warum kein App Store / signiertes MSIX?**
Ausgeliefert wird vorerst ein portables EXE + PowerShell-Installer, dieselbe
„klonen und installieren"-Philosophie wie bei der Mac-App. MSIX-Sandboxing würde
außerdem die rohe Process-Loopback-COM-Aktivierung erschweren, auf der dieses
Projekt aufbaut.

## Datenschutz & Recht

Standardmäßig läuft alles lokal (Whisper on-device); nur die Zusammenfassung geht
— falls aktiviert — an die von dir gewählte KI, Transkription an Groq nur per
Opt-in. **Informiere deine Gesprächspartner über die Aufnahme.** Die Rechtslage
ist je Land unterschiedlich (in Österreich ist z. B. die *Weitergabe* heimlicher
Aufnahmen strafbar, § 120 StGB; in Deutschland schon die heimliche *Aufnahme*,
§ 201 StGB). Für die rechtmäßige Nutzung bist du selbst verantwortlich.

## Lizenz

[PolyForm Noncommercial 1.0.0](LICENSE) — frei für private und nichtkommerzielle
Nutzung. **Verkauf und kommerzielle Nutzung sind nicht erlaubt.**

---

<p align="center"><sub><a href="README.md">🇬🇧 This page in English</a> · <a href="https://github.com/michaelczesun/callnotes">Das Original: CallNotes für macOS</a></sub></p>

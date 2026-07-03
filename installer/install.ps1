#Requires -Version 5.1
<#
.SYNOPSIS
    install.ps1 - CallNotes / calltap unter Windows komplett einrichten.

.DESCRIPTION
    Portierung von /Users/michaelczesun/Documents/callnotes/install.sh auf Windows.
    Schritte (siehe docs/contract.md Abschnitt "installer/"):
      1) Abhaengigkeiten pruefen/holen (ffmpeg, python via winget; .NET 8 SDK nur Hinweis)
      2) whisper.cpp-Windows-Release (whisper-cli.exe) + ggml-large-v3-turbo-q5_0.bin laden
      3) sherpa-onnx + Diarisierungs-Modelle in venv (%LOCALAPPDATA%\callnotes\venv)
      4) dotnet build -c Release (CallTap.sln)
      5) config.json aus config.example.json anlegen (postScript/Pfade setzen)
      6) Autostart fuer calltap.exe watch + CallNotesTray via Scheduled Task (schtasks /sc onlogon)
      7) Beide Prozesse sofort starten

    Alle Downloads/URLs sind 1:1 aus dem macOS-Original uebernommen
    (/Users/michaelczesun/Documents/callnotes/install.sh), nur Pfad-Konventionen
    sind Windows-typisch (%APPDATA%, %USERPROFILE%, %LOCALAPPDATA%).

.NOTES
    PowerShell 5.1-kompatibel (kein ternaerer Operator, kein "??", kein pwsh-only Syntax).
    Muss aus dem Repo-Root des Windows-Ports heraus lauffaehig sein: installer\install.ps1
    wird relativ zum Repo-Root aufgeloest (Parent von $PSScriptRoot).
#>

[CmdletBinding()]
param(
    # Ueberspringt den dotnet build-Schritt (z.B. wenn schon gebaut wurde / CI).
    [switch]$SkipBuild,

    # Ueberspringt Download der grossen Modelle (whisper + Diarisierung). Fuer schnelle Reinstalls.
    [switch]$SkipModels
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"  # macht Invoke-WebRequest deutlich schneller

# ------------------------------------------------------------------------------------
# 0) Grundpfade
# ------------------------------------------------------------------------------------

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$CfgDir     = Join-Path $env:APPDATA "callnotes"
$Cfg        = Join-Path $CfgDir "config.json"
$CfgExample = Join-Path $RepoRoot "config.example.json"

$DataRoot   = Join-Path $env:USERPROFILE "CallNotes"
$RecDir     = Join-Path $DataRoot "rec"
$LogDir     = Join-Path $DataRoot "log"
$AudioDir   = Join-Path $DataRoot "audio"
$FailedDir  = Join-Path $DataRoot "failed"
$NotesDir   = Join-Path $DataRoot "notes"
$StateDir   = Join-Path $DataRoot "state"
$PendingDir = Join-Path $StateDir "pending"
$ReviewDir  = Join-Path $DataRoot "review"

$LocalAppData = Join-Path $env:LOCALAPPDATA "callnotes"
$Venv         = Join-Path $LocalAppData "venv"
$VenvPython   = Join-Path $Venv "Scripts\python.exe"
$DiaModels    = Join-Path $LocalAppData "models"
$ToolsDir     = Join-Path $LocalAppData "tools"
$WhisperDir   = Join-Path $ToolsDir "whisper-cpp"

# Tatsaechliche Projektordner/Binary-Namen fuer diesen Auftrag (siehe CallTap.sln
# im Repo-Root): src\CallTap\CallTap.csproj -> calltap.exe, src\CallNotesTray\
# CallNotesTray.csproj -> CallNotesTray.exe. Der volle Langfrist-Contract
# (CallTap.Core/CallTap.Cli/CallTap.Tray-Aufteilung, Contract Abschnitt 2) ist
# noch nicht umgesetzt.
#
# CallTap.csproj setzt RuntimeIdentifier=win-x64 bereits fuer JEDE Release-
# Konfiguration (nicht nur bei "dotnet publish"), siehe PublishSingleFile/
# SelfContained/RuntimeIdentifier-Conditions dort — deshalb landet calltap.exe
# nach "dotnet build -c Release" unter bin\Release\net8.0-windows\win-x64\,
# NICHT direkt unter bin\Release\net8.0-windows\. CallNotesTray.csproj setzt
# kein RuntimeIdentifier, bleibt also im framework-abhaengigen Pfad ohne RID-
# Unterordner.
$SolutionPath = Join-Path $RepoRoot "CallTap.sln"
$CliExe       = Join-Path $RepoRoot "src\CallTap\bin\Release\net8.0-windows\win-x64\calltap.exe"
$TrayExe      = Join-Path $RepoRoot "src\CallNotesTray\bin\Release\net8.0-windows\CallNotesTray.exe"

Write-Host "== CallNotes install (Windows) ==" -ForegroundColor Cyan

# ------------------------------------------------------------------------------------
# Hilfsfunktionen
# ------------------------------------------------------------------------------------

function Test-CommandExists {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    return ($null -ne $cmd)
}

function Invoke-DownloadWithRetry {
    <#
        Wget-Aequivalent zu "curl -sL --retry N -o out url" aus install.sh.
        Bricht NICHT den ganzen Installer ab bei Fehlschlag, gibt $false zurueck
        und schreibt eine WARNUNG (gleiche "warn, don't crash"-Haltung wie macOS).
    #>
    param(
        [string]$Url,
        [string]$OutFile,
        [int]$Retries = 5
    )
    for ($i = 1; $i -le $Retries; $i++) {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
            return $true
        } catch {
            Write-Host "  Download-Versuch $i/$Retries fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor DarkYellow
            Start-Sleep -Seconds 2
        }
    }
    return $false
}

function Expand-TarBz2 {
    <#
        .tar.bz2 auspacken ohne externe 7zip-Abhaengigkeit: seit Windows 10 1803 kann
        das eingebaute bsdtar (als "tar" im PATH, System32\tar.exe) .tar.bz2 direkt.
    #>
    param([string]$ArchivePath, [string]$DestDir)
    if (-not (Test-CommandExists "tar")) {
        Write-Host "  WARNUNG: 'tar' nicht gefunden (sollte seit Win10 1803 eingebaut sein) — kann $ArchivePath nicht auspacken." -ForegroundColor DarkYellow
        return $false
    }
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
    & tar -xjf $ArchivePath -C $DestDir
    return ($LASTEXITCODE -eq 0)
}

function Expand-ZipArchive {
    param([string]$ArchivePath, [string]$DestDir)
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
    Expand-Archive -Path $ArchivePath -DestinationPath $DestDir -Force
}

# ------------------------------------------------------------------------------------
# 1) Abhaengigkeiten pruefen/holen
# ------------------------------------------------------------------------------------

Write-Host "`n[1/7] Abhaengigkeiten pruefen..." -ForegroundColor Cyan

$missing = @()

if (-not (Test-CommandExists "ffmpeg")) {
    if (Test-CommandExists "winget") {
        Write-Host "  ffmpeg fehlt -> installiere via winget..."
        try {
            winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements
        } catch {
            Write-Host "  WARNUNG: winget-Installation von ffmpeg fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor DarkYellow
            $missing += "ffmpeg (manuell: winget install Gyan.FFmpeg, oder https://ffmpeg.org)"
        }
    } else {
        $missing += "ffmpeg (winget fehlt selbst -> manuell von https://ffmpeg.org installieren und in PATH aufnehmen)"
    }
}

if (-not (Test-CommandExists "python")) {
    if (Test-CommandExists "winget") {
        Write-Host "  python fehlt -> installiere via winget..."
        try {
            winget install --id Python.Python.3.12 -e --accept-source-agreements --accept-package-agreements
        } catch {
            Write-Host "  WARNUNG: winget-Installation von python fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor DarkYellow
            $missing += "python (manuell: winget install Python.Python.3.12, oder https://python.org)"
        }
    } else {
        $missing += "python (winget fehlt selbst -> manuell von https://python.org installieren)"
    }
}

# .NET 8 SDK ist zum Bauen (dotnet build) noetig, wird NICHT automatisch installiert
# (grosser Download, Nutzer soll bewusst entscheiden) — nur Hinweis wie bei swiftc auf macOS.
$dotnetOk = $false
if (Test-CommandExists "dotnet") {
    $sdks = & dotnet --list-sdks 2>$null
    if ($sdks -match '^8\.') { $dotnetOk = $true }
}
if (-not $dotnetOk) {
    Write-Host "  HINWEIS: .NET 8 SDK nicht gefunden (dotnet --list-sdks zeigt keine 8.x-Version)." -ForegroundColor Yellow
    Write-Host "  Bitte manuell installieren: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    if (Test-CommandExists "winget") {
        Write-Host "  Oder: winget install Microsoft.DotNet.SDK.8" -ForegroundColor Yellow
    }
    if (-not $SkipBuild) {
        $missing += ".NET 8 SDK (winget install Microsoft.DotNet.SDK.8, oder https://dotnet.microsoft.com/download/dotnet/8.0)"
    }
}

if ($missing.Count -gt 0) {
    Write-Host "`nFEHLT (bitte nachinstallieren und install.ps1 erneut ausfuehren):" -ForegroundColor Red
    foreach ($m in $missing) { Write-Host "  - $m" -ForegroundColor Red }
    exit 1
}

# ------------------------------------------------------------------------------------
# 2) Verzeichnisstruktur anlegen
# ------------------------------------------------------------------------------------

Write-Host "`n[2/7] Verzeichnisse anlegen..." -ForegroundColor Cyan

$dirsToCreate = @(
    $CfgDir, $DataRoot, $RecDir, $LogDir, $AudioDir, $FailedDir,
    $NotesDir, $StateDir, $PendingDir, $ReviewDir,
    $LocalAppData, $DiaModels, $ToolsDir, $WhisperDir
)
foreach ($d in $dirsToCreate) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}
Write-Host "  Datenordner: $DataRoot"
Write-Host "  Config-Ordner: $CfgDir"

# ------------------------------------------------------------------------------------
# 3) whisper.cpp-Windows-Release (whisper-cli.exe) laden
# ------------------------------------------------------------------------------------

Write-Host "`n[3/7] whisper.cpp (whisper-cli.exe)..." -ForegroundColor Cyan

$WhisperCliExe = Join-Path $WhisperDir "whisper-cli.exe"
if ((Test-Path $WhisperCliExe) -and -not $SkipModels) {
    Write-Host "  whisper-cli.exe bereits vorhanden: $WhisperCliExe"
} elseif (-not $SkipModels) {
    Write-Host "  Lade whisper.cpp Windows-Release (whisper-cli.exe)..."
    # Aktuelles CUDA-freies CPU-Release von whisper.cpp fuer Windows x64.
    # Analog zum Mac-Weg "brew install whisper-cpp" — hier direkt vom offiziellen
    # GitHub-Release-Asset, da es kein winget-Paket fuer whisper.cpp gibt.
    $whisperZipUrl = "https://github.com/ggml-org/whisper.cpp/releases/latest/download/whisper-bin-x64.zip"
    $whisperZip = Join-Path $ToolsDir "whisper-bin-x64.zip"
    $ok = Invoke-DownloadWithRetry -Url $whisperZipUrl -OutFile $whisperZip -Retries 4
    if ($ok) {
        try {
            Expand-ZipArchive -ArchivePath $whisperZip -DestDir $WhisperDir
            Remove-Item $whisperZip -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path $WhisperCliExe)) {
                # Manche Releases packen in einen Unterordner — einmal nachsuchen und hochkopieren.
                $found = Get-ChildItem -Path $WhisperDir -Recurse -Filter "whisper-cli.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($found) {
                    Copy-Item $found.FullName $WhisperCliExe -Force
                }
            }
            if (Test-Path $WhisperCliExe) {
                Write-Host "  whisper-cli.exe installiert: $WhisperCliExe" -ForegroundColor Green
            } else {
                Write-Host "  WARNUNG: whisper-cli.exe nach dem Entpacken nicht gefunden — manuell pruefen: $WhisperDir" -ForegroundColor DarkYellow
            }
        } catch {
            Write-Host "  WARNUNG: Entpacken von whisper.cpp fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "  WARNUNG: whisper.cpp-Download fehlgeschlagen. Manuell: https://github.com/ggml-org/whisper.cpp/releases" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  uebersprungen (-SkipModels)"
}

# Whisper-Modell (~550 MB) — identische Quelle wie macOS install.sh.
$ModelsDir = Join-Path $env:USERPROFILE "models"
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
$WhisperModelPath = Join-Path $ModelsDir "ggml-large-v3-turbo-q5_0.bin"
if ((Test-Path $WhisperModelPath) -and -not $SkipModels) {
    Write-Host "  Whisper-Modell bereits vorhanden: $WhisperModelPath"
} elseif (-not $SkipModels) {
    Write-Host "  Lade Whisper-Modell (~550 MB) — kann etwas dauern..."
    $ok = Invoke-DownloadWithRetry -Url "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin" -OutFile $WhisperModelPath -Retries 4
    if ($ok) {
        Write-Host "  Whisper-Modell geladen: $WhisperModelPath" -ForegroundColor Green
    } else {
        Write-Host "  WARNUNG: Whisper-Modell-Download fehlgeschlagen. Spaeter manuell nachholen:" -ForegroundColor DarkYellow
        Write-Host "    Invoke-WebRequest -Uri https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin -OutFile `"$WhisperModelPath`"" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  uebersprungen (-SkipModels)"
}

# ------------------------------------------------------------------------------------
# 4) sherpa-onnx (Diarisierung): venv + Modelle — identische Quellen wie macOS install.sh
# ------------------------------------------------------------------------------------

Write-Host "`n[4/7] Diarisierungs-Umgebung (sherpa-onnx)..." -ForegroundColor Cyan

if (-not (Test-Path $VenvPython)) {
    Write-Host "  Richte venv ein: $Venv"
    try {
        & python -m venv $Venv
        & $VenvPython -m pip install -q --upgrade pip
        & $VenvPython -m pip install -q sherpa-onnx numpy
        Write-Host "  venv + sherpa-onnx + numpy installiert." -ForegroundColor Green
    } catch {
        Write-Host "  WARNUNG: venv/sherpa-onnx-Einrichtung fehlgeschlagen — Diarisierung deaktiviert (1:1-Anrufe gehen trotzdem): $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  venv bereits vorhanden: $Venv"
}

$SegModelPath = Join-Path $DiaModels "sherpa-onnx-pyannote-segmentation-3-0\model.onnx"
if ((Test-Path $SegModelPath) -and -not $SkipModels) {
    Write-Host "  Segmentierungs-Modell bereits vorhanden."
} elseif (-not $SkipModels) {
    Write-Host "  Lade Segmentierungs-Modell (~9 MB)..."
    $segArchive = Join-Path $DiaModels "seg.tar.bz2"
    $ok = Invoke-DownloadWithRetry -Url "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2" -OutFile $segArchive -Retries 4
    if ($ok) {
        $extracted = Expand-TarBz2 -ArchivePath $segArchive -DestDir $DiaModels
        Remove-Item $segArchive -Force -ErrorAction SilentlyContinue
        if (-not $extracted) {
            Write-Host "  WARNUNG: Segmentierungs-Modell konnte nicht entpackt werden." -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "  WARNUNG: Segmentierungs-Modell-Download fehlgeschlagen." -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  uebersprungen (-SkipModels)"
}

$EmbedModelPath = Join-Path $DiaModels "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx"
if ((Test-Path $EmbedModelPath) -and -not $SkipModels) {
    Write-Host "  Sprecher-Embedding-Modell bereits vorhanden."
} elseif (-not $SkipModels) {
    Write-Host "  Lade Sprecher-Embedding-Modell (~26 MB)..."
    $ok = Invoke-DownloadWithRetry -Url "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx" -OutFile $EmbedModelPath -Retries 5
    if (-not $ok) {
        Write-Host "  WARNUNG: Embedding-Download fehlgeschlagen." -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  uebersprungen (-SkipModels)"
}

# ------------------------------------------------------------------------------------
# 5) pipeline/ Python-Abhaengigkeiten (requirements.txt) in dasselbe venv
# ------------------------------------------------------------------------------------

$Requirements = Join-Path $RepoRoot "pipeline\requirements.txt"
if ((Test-Path $Requirements) -and (Test-Path $VenvPython)) {
    Write-Host "`n  Installiere pipeline/requirements.txt in venv..."
    try {
        & $VenvPython -m pip install -q -r $Requirements
    } catch {
        Write-Host "  WARNUNG: pip install -r requirements.txt fehlgeschlagen: $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
}

# ------------------------------------------------------------------------------------
# 6) dotnet build -c Release
# ------------------------------------------------------------------------------------

Write-Host "`n[5/7] dotnet build -c Release..." -ForegroundColor Cyan

if ($SkipBuild) {
    Write-Host "  uebersprungen (-SkipBuild)"
} else {
    if (-not (Test-Path $SolutionPath)) {
        Write-Host "  WARNUNG: $SolutionPath nicht gefunden — Build uebersprungen. Bitte Repo-Struktur pruefen." -ForegroundColor DarkYellow
    } else {
        Push-Location $RepoRoot
        try {
            & dotnet build $SolutionPath -c Release
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet build ist mit Exit-Code $LASTEXITCODE fehlgeschlagen."
            }
            Write-Host "  Build erfolgreich." -ForegroundColor Green
        } finally {
            Pop-Location
        }
    }
}

# ------------------------------------------------------------------------------------
# 7) config.json anlegen (bestehende bleibt unangetastet), Pfade + postScript setzen
# ------------------------------------------------------------------------------------

Write-Host "`n[6/7] Konfiguration..." -ForegroundColor Cyan

if (-not (Test-Path $Cfg)) {
    Copy-Item $CfgExample $Cfg
    Write-Host "  Config angelegt: $Cfg"
} else {
    Write-Host "  Config existiert bereits, bleibt unangetastet: $Cfg"
}

# postScript auf process_call.py in DIESEM Repo zeigen lassen + absolute Pfade
# (whisperModel/venvPython) setzen — Windows-Analogon zum Python-Block in install.sh.
# Reines .NET/PowerShell JSON-Handling statt externem Python-Aufruf, damit dieser
# Schritt keine zusaetzliche Python-Abhaengigkeit zur Laufzeit des Installers braucht.
try {
    $json = Get-Content $Cfg -Raw | ConvertFrom-Json

    $postScriptPath = Join-Path $RepoRoot "pipeline\process_call.py"
    $venvPythonForJson = $VenvPython

    # ConvertFrom-Json liefert ein PSCustomObject — Eigenschaften per Add-Member/direkter
    # Zuweisung setzen (funktioniert in PS 5.1 fuer bereits vorhandene Properties).
    $json.postScript = $postScriptPath
    $json.venvPython = $venvPythonForJson
    $json.whisperModel = $WhisperModelPath

    if ($json.PSObject.Properties.Name -contains "_hinweis") {
        $json.PSObject.Properties.Remove("_hinweis")
    }

    $json | ConvertTo-Json -Depth 10 | Set-Content -Path $Cfg -Encoding UTF8
    Write-Host "  postScript/whisperModel/venvPython in config.json gesetzt."
} catch {
    Write-Host "  WARNUNG: config.json konnte nicht automatisch aktualisiert werden: $($_.Exception.Message)" -ForegroundColor DarkYellow
    Write-Host "  Bitte postScript/whisperModel/venvPython manuell in $Cfg pruefen." -ForegroundColor DarkYellow
}

if (-not (Test-Path $WhisperModelPath)) {
    Write-Host "  HINWEIS: Whisper-Modell fehlt noch: $WhisperModelPath" -ForegroundColor Yellow
    Write-Host "    Nachtraeglich laden: Invoke-WebRequest -Uri https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin -OutFile `"$WhisperModelPath`"" -ForegroundColor Yellow
}

# ------------------------------------------------------------------------------------
# 8) Autostart: Scheduled Task fuer calltap.exe watch + CallNotesTray (schtasks /sc onlogon)
# ------------------------------------------------------------------------------------

Write-Host "`n[7/7] Autostart registrieren (Task Scheduler)..." -ForegroundColor Cyan

$WatchTaskName = "CallNotes-Watch"
$TrayTaskName  = "CallNotes-Tray"

if (-not (Test-Path $CliExe)) {
    Write-Host "  WARNUNG: $CliExe nicht gefunden — Watch-Task wird trotzdem registriert (zeigt beim naechsten Login Fehler falls Build fehlt)." -ForegroundColor DarkYellow
}
if (-not (Test-Path $TrayExe)) {
    Write-Host "  WARNUNG: $TrayExe nicht gefunden — Tray-Task wird trotzdem registriert." -ForegroundColor DarkYellow
}

# Vorhandene Tasks (falls Reinstall) sauber entfernen, damit /create nicht mit
# "task already exists" scheitert — Analogon zu launchctl bootout vor bootstrap.
& schtasks /query /tn $WatchTaskName 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    & schtasks /end /tn $WatchTaskName 2>$null | Out-Null
    & schtasks /delete /tn $WatchTaskName /f 2>$null | Out-Null
}
& schtasks /query /tn $TrayTaskName 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    & schtasks /end /tn $TrayTaskName 2>$null | Out-Null
    & schtasks /delete /tn $TrayTaskName /f 2>$null | Out-Null
}

# calltap.exe watch: headless Daemon, Log nach %USERPROFILE%\CallNotes\log\callwatch.log
$WatchLog = Join-Path $LogDir "callwatch.log"
$WatchCmd = "`"$CliExe`" watch >> `"$WatchLog`" 2>&1"
$WatchTr = & schtasks /create /tn $WatchTaskName /sc onlogon /rl limited /it `
    /tr "cmd.exe /c $WatchCmd" 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  Task '$WatchTaskName' registriert (startet calltap.exe watch bei Login)." -ForegroundColor Green
} else {
    Write-Host "  WARNUNG: Task '$WatchTaskName' konnte nicht registriert werden: $WatchTr" -ForegroundColor DarkYellow
}

# CallNotesTray.exe: interaktive Tray-App
$TrayTr = & schtasks /create /tn $TrayTaskName /sc onlogon /rl limited /it `
    /tr "`"$TrayExe`"" 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  Task '$TrayTaskName' registriert (startet CallNotesTray.exe bei Login)." -ForegroundColor Green
} else {
    Write-Host "  WARNUNG: Task '$TrayTaskName' konnte nicht registriert werden: $TrayTr" -ForegroundColor DarkYellow
}

# ------------------------------------------------------------------------------------
# 9) Beide Prozesse sofort starten (nicht erst beim naechsten Login warten)
# ------------------------------------------------------------------------------------

Write-Host "`nStarte Dienste..." -ForegroundColor Cyan

# Laufende Instanzen sauber stoppen bevor neu gestartet wird (Reinstall-Fall).
Get-Process -Name "calltap" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "CallNotesTray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (Test-Path $CliExe) {
    try {
        Start-Process -FilePath $CliExe -ArgumentList "watch" -WindowStyle Hidden `
            -RedirectStandardOutput $WatchLog -RedirectStandardError (Join-Path $LogDir "callwatch.err.log")
        Write-Host "  calltap.exe watch gestartet. Log: $WatchLog" -ForegroundColor Green
    } catch {
        Write-Host "  WARNUNG: calltap.exe watch konnte nicht gestartet werden: $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  WARNUNG: calltap.exe fehlt — Watch-Daemon nicht gestartet (Build pruefen: dotnet build $SolutionPath -c Release)." -ForegroundColor DarkYellow
}

if (Test-Path $TrayExe) {
    try {
        Start-Process -FilePath $TrayExe
        Write-Host "  CallNotesTray.exe gestartet (Symbol unten rechts im Infobereich)." -ForegroundColor Green
    } catch {
        Write-Host "  WARNUNG: CallNotesTray.exe konnte nicht gestartet werden: $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
} else {
    Write-Host "  WARNUNG: CallNotesTray.exe fehlt — Tray-App nicht gestartet." -ForegroundColor DarkYellow
}

Start-Sleep -Seconds 2
$watchRunning = $null -ne (Get-Process -Name "calltap" -ErrorAction SilentlyContinue)
if ($watchRunning) {
    Write-Host "`nDaemon laeuft (calltap.exe watch). Log: $WatchLog" -ForegroundColor Green
} else {
    Write-Host "`nWARNUNG: Daemon laeuft nicht (mehr) — Log pruefen: $WatchLog" -ForegroundColor DarkYellow
}

# ------------------------------------------------------------------------------------
# 10) Abschluss-Hinweise (Mikrofon-Freigabe = Windows-Analogon zu macOS TCC-Dialog)
# ------------------------------------------------------------------------------------

Write-Host ""
Write-Host "WICHTIG: Beim ersten Start fragt Windows nach der Mikrofon-Freigabe fuer" -ForegroundColor Yellow
Write-Host "'calltap'/Desktop-Apps — bitte erlauben. Falls kein Dialog kam, manuell pruefen:" -ForegroundColor Yellow
Write-Host "Einstellungen > Datenschutz und Sicherheit > Mikrofon >" -ForegroundColor Yellow
Write-Host "'Desktop-Apps den Zugriff auf das Mikrofon erlauben' aktivieren." -ForegroundColor Yellow
Write-Host ""
Write-Host "Systemaudio-Aufnahme (Process Loopback) braucht Windows 10 Build 20348+" -ForegroundColor Yellow
Write-Host "bzw. Windows 11 — 'calltap.exe setup' zeigt, ob die API auf diesem Rechner geht." -ForegroundColor Yellow
Write-Host ""
Write-Host "Einstellungen (Speicherorte, Apps-Liste): $Cfg" -ForegroundColor Cyan
Write-Host "Fertig." -ForegroundColor Cyan

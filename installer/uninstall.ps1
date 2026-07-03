#Requires -Version 5.1
<#
.SYNOPSIS
    uninstall.ps1 - CallNotes / calltap unter Windows sauber entfernen.

.DESCRIPTION
    Gegenstueck zu install.ps1:
      1) Watch-Daemon + Tray-App stoppen (Prozesse)
      2) Scheduled Tasks entfernen (CallNotes-Watch, CallNotes-Tray)
      3) Optional: Konfiguration (%APPDATA%\callnotes) entfernen (-RemoveConfig)
      4) Optional: Aufgenommene Notizen/Audios (%USERPROFILE%\CallNotes) entfernen (-RemoveData)
      5) Optional: venv + Modelle (%LOCALAPPDATA%\callnotes) entfernen (-RemoveModels)

    Standardmaessig werden NUR Prozesse + Autostart-Tasks entfernt — Notizen, Audios
    und heruntergeladene Modelle bleiben erhalten, damit ein versehentliches
    "uninstall.ps1" ohne Parameter nicht persoenliche Anrufnotizen loescht.
    Das mirrort die vorsichtige macOS-Haltung (kein aequivalentes uninstall.sh im
    Original, aber gleiche Grundregel: Nutzerdaten nie ungefragt weg).

.NOTES
    PowerShell 5.1-kompatibel.
#>

[CmdletBinding()]
param(
    # Entfernt %APPDATA%\callnotes (config.json, secrets.env).
    [switch]$RemoveConfig,

    # Entfernt %USERPROFILE%\CallNotes (Notizen, Audios, State) — ECHTER Datenverlust, mit Rueckfrage.
    [switch]$RemoveData,

    # Entfernt %LOCALAPPDATA%\callnotes (venv, Diarisierungs-Modelle, whisper.cpp-Binary).
    [switch]$RemoveModels,

    # Ueberspringt alle Rueckfragen (fuer nicht-interaktive Deinstallation, z.B. CI).
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "== CallNotes uninstall (Windows) ==" -ForegroundColor Cyan

$CfgDir     = Join-Path $env:APPDATA "callnotes"
$DataRoot   = Join-Path $env:USERPROFILE "CallNotes"
$LocalAppData = Join-Path $env:LOCALAPPDATA "callnotes"

$WatchTaskName = "CallNotes-Watch"
$TrayTaskName  = "CallNotes-Tray"

# ------------------------------------------------------------------------------------
# 1) Prozesse stoppen
# ------------------------------------------------------------------------------------

Write-Host "`n[1/4] Stoppe laufende Prozesse..." -ForegroundColor Cyan

$procsStopped = 0
foreach ($procName in @("calltap", "CallNotesTray")) {
    $procs = Get-Process -Name $procName -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        $procsStopped += $procs.Count
        Write-Host "  $procName gestoppt ($($procs.Count) Prozess(e))."
    }
}
if ($procsStopped -eq 0) {
    Write-Host "  Keine laufenden CallNotes-Prozesse gefunden."
}

# ------------------------------------------------------------------------------------
# 2) Scheduled Tasks entfernen
# ------------------------------------------------------------------------------------

Write-Host "`n[2/4] Entferne Autostart-Tasks..." -ForegroundColor Cyan

foreach ($taskName in @($WatchTaskName, $TrayTaskName)) {
    & schtasks /query /tn $taskName 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & schtasks /end /tn $taskName 2>$null | Out-Null
        & schtasks /delete /tn $taskName /f 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Task '$taskName' entfernt." -ForegroundColor Green
        } else {
            Write-Host "  WARNUNG: Task '$taskName' konnte nicht entfernt werden." -ForegroundColor DarkYellow
        }
    } else {
        Write-Host "  Task '$taskName' war nicht registriert, uebersprungen."
    }
}

# ------------------------------------------------------------------------------------
# Hilfsfunktion fuer Rueckfragen
# ------------------------------------------------------------------------------------

function Confirm-Removal {
    param([string]$Path, [string]$Description)
    if ($Force) { return $true }
    if (-not (Test-Path $Path)) { return $false }
    $answer = Read-Host "  $Description ($Path) wirklich loeschen? [j/N]"
    return ($answer -eq "j" -or $answer -eq "J" -or $answer -eq "y" -or $answer -eq "Y")
}

# ------------------------------------------------------------------------------------
# 3) Optional: Konfiguration entfernen
# ------------------------------------------------------------------------------------

Write-Host "`n[3/4] Konfiguration..." -ForegroundColor Cyan

if ($RemoveConfig) {
    if (Test-Path $CfgDir) {
        if (Confirm-Removal -Path $CfgDir -Description "Konfiguration (config.json, secrets.env)") {
            Remove-Item -Path $CfgDir -Recurse -Force
            Write-Host "  Entfernt: $CfgDir" -ForegroundColor Green
        } else {
            Write-Host "  Uebersprungen (nicht bestaetigt)."
        }
    } else {
        Write-Host "  Kein Konfigurationsordner vorhanden: $CfgDir"
    }
} else {
    Write-Host "  Bleibt erhalten (mit -RemoveConfig entfernen): $CfgDir"
}

# ------------------------------------------------------------------------------------
# 4) Optional: Daten (Notizen/Audios) + Modelle/venv entfernen
# ------------------------------------------------------------------------------------

Write-Host "`n[4/4] Daten und Modelle..." -ForegroundColor Cyan

if ($RemoveData) {
    if (Test-Path $DataRoot) {
        if (Confirm-Removal -Path $DataRoot -Description "ALLE Anrufnotizen, Audios und States") {
            Remove-Item -Path $DataRoot -Recurse -Force
            Write-Host "  Entfernt: $DataRoot" -ForegroundColor Green
        } else {
            Write-Host "  Uebersprungen (nicht bestaetigt) — Notizen/Audios bleiben erhalten."
        }
    } else {
        Write-Host "  Kein Datenordner vorhanden: $DataRoot"
    }
} else {
    Write-Host "  Bleibt erhalten (mit -RemoveData entfernen, LOESCHT NOTIZEN/AUDIOS!): $DataRoot"
}

if ($RemoveModels) {
    if (Test-Path $LocalAppData) {
        if (Confirm-Removal -Path $LocalAppData -Description "venv, whisper.cpp-Binary, Diarisierungs-Modelle (grosser Re-Download noetig)") {
            Remove-Item -Path $LocalAppData -Recurse -Force
            Write-Host "  Entfernt: $LocalAppData" -ForegroundColor Green
        } else {
            Write-Host "  Uebersprungen (nicht bestaetigt)."
        }
    } else {
        Write-Host "  Kein Modell-/venv-Ordner vorhanden: $LocalAppData"
    }
} else {
    Write-Host "  Bleibt erhalten (mit -RemoveModels entfernen): $LocalAppData"
}

# Hinweis: Das grosse Whisper-Modell (~550 MB) liegt separat unter
# %USERPROFILE%\models\ggml-large-v3-turbo-q5_0.bin (konfigurierbar) und wird
# bewusst NICHT automatisch geloescht, da es ggf. von anderen Whisper-Tools
# mitgenutzt wird — nur erwaehnen, nicht anfassen.
$WhisperModelPath = Join-Path $env:USERPROFILE "models\ggml-large-v3-turbo-q5_0.bin"
if (Test-Path $WhisperModelPath) {
    Write-Host "`nHINWEIS: Whisper-Modell liegt noch unter $WhisperModelPath (~550 MB) —" -ForegroundColor Yellow
    Write-Host "wird von uninstall.ps1 nicht angefasst (koennte von anderen Tools genutzt werden)." -ForegroundColor Yellow
    Write-Host "Manuell loeschen: Remove-Item `"$WhisperModelPath`"" -ForegroundColor Yellow
}

Write-Host "`nFertig." -ForegroundColor Cyan

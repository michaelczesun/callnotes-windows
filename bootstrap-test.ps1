# bootstrap-test.ps1 — CallNotes for Windows: Abhaengigkeiten holen, bauen, Smoke-Test.
# In der Windows-VM (PowerShell) mit EINER Zeile ausfuehren:
#   irm https://raw.githubusercontent.com/michaelczesun/callnotes-windows/main/bootstrap-test.ps1 | iex
$ErrorActionPreference = 'Stop'
Write-Host "== CallNotes for Windows - Test-Bootstrap ==" -ForegroundColor Cyan

function Have($cmd) { [bool](Get-Command $cmd -ErrorAction SilentlyContinue) }

# 1) Abhaengigkeiten (winget ist auf Windows 11 vorhanden)
if (-not (Have git)) {
    Write-Host "Installiere Git ..." -ForegroundColor Yellow
    winget install --id Git.Git -e --accept-package-agreements --accept-source-agreements
}
if (-not (Have dotnet)) {
    Write-Host "Installiere .NET 8 SDK ..." -ForegroundColor Yellow
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
}

# PATH fuer DIESE Sitzung auffrischen (winget aktualisiert nur neue Fenster)
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [Environment]::GetEnvironmentVariable("Path", "User")

if (-not (Have git) -or -not (Have dotnet)) {
    Write-Host "`nGit/.NET wurden installiert, sind in DIESER Sitzung aber noch nicht im PATH." -ForegroundColor Yellow
    Write-Host "Schliesse dieses Fenster, oeffne PowerShell NEU und fuehre denselben Befehl noch einmal aus." -ForegroundColor Yellow
    return
}

# 2) Repo holen/aktualisieren
$dir = Join-Path $HOME "callnotes-windows"
if (Test-Path (Join-Path $dir ".git")) {
    Write-Host "Aktualisiere vorhandenes Repo ..." -ForegroundColor Yellow
    git -C $dir pull --ff-only
} else {
    Write-Host "Clone Repo ..." -ForegroundColor Yellow
    git clone https://github.com/michaelczesun/callnotes-windows $dir
}
Set-Location $dir

# 3) Bauen (beide Projekte, Release)
Write-Host "`nBaue CallTap (Recorder-CLI) ..." -ForegroundColor Yellow
dotnet build src\CallTap\CallTap.csproj -c Release
Write-Host "Baue CallNotesTray (Tray-App) ..." -ForegroundColor Yellow
dotnet build src\CallNotesTray\CallNotesTray.csproj -c Release

# 4) Smoke-Test: der erste echte Lauf auf Windows.
#    'calltap procs' zaehlt via WASAPI die Audio-Sessions auf und zeigt, welche
#    Prozesse gerade das Mikrofon nutzen. Laeuft das ohne Absturz, ist der
#    Core-Audio-Interop grundsaetzlich intakt.
$exe = Get-ChildItem -Recurse -Filter calltap.exe -Path (Join-Path $dir "src\CallTap\bin\Release") |
       Select-Object -First 1
if (-not $exe) { Write-Host "calltap.exe nicht gefunden - Build fehlgeschlagen?" -ForegroundColor Red; return }

Write-Host "`n== calltap procs (Audio-Prozesse; MIC = nutzt gerade das Mikrofon) ==" -ForegroundColor Cyan
& $exe.FullName procs

Write-Host "`nErfolg, wenn oben eine Prozessliste ohne Absturz erscheint." -ForegroundColor Green
Write-Host "Naechster echter Test: irgendeine Audio-App abspielen lassen, dann in einem" -ForegroundColor Green
Write-Host "zweiten Fenster '$($exe.FullName) record --out `$HOME\rec --seconds 12' - danach" -ForegroundColor Green
Write-Host "pruefen ob `$HOME\rec\system.wav NICHT stumm ist (das ist der WASAPI-Loopback-Beweis)." -ForegroundColor Green
Write-Host "`nBericht bitte als Issue: https://github.com/michaelczesun/callnotes-windows/issues" -ForegroundColor Cyan

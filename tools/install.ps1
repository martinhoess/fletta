# Installiert Fletta fuer den angemeldeten Benutzer: Publish (ReadyToRun) nach
# %LOCALAPPDATA%\Programs\Fletta, Anmeldung als PDF-Programm, dann die Standard-Apps oeffnen.
# Laufende Fletta-Fenster aus diesem Ordner werden dafuer geschlossen (die EXE ist sonst gesperrt).
param([string]$Source = (Split-Path $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Fletta'

Get-Process Fletta -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target\*" } | Stop-Process -Force
dotnet publish "$Source\src\Fletta.csproj" -c Release -o $target --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$p = Start-Process "$target\Fletta.exe" -ArgumentList '--register' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Fletta --register meldet $($p.ExitCode)" }

# Den Standard setzt Windows nur ueber die Einstellungen: dort .pdf auf Fletta stellen.
Start-Process 'ms-settings:defaultapps'
"Fletta installiert nach $target und als PDF-Programm angemeldet."

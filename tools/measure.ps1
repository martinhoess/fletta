# Veroeffentlicht Fletta (ReadyToRun) und misst den Start mit einer PDF. Oeffnet dafuer kurz Fenster.
param(
    [Parameter(Mandatory)][string]$Pdf,
    [int]$Runs = 5
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
dotnet publish "$root\src\Fletta.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = "$root\src\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\Fletta.exe"
foreach ($run in 1..$Runs) {
    $log = New-TemporaryFile
    Start-Process $exe -ArgumentList '--measure', "`"$Pdf`"" -Wait -NoNewWindow -RedirectStandardError $log.FullName
    "Lauf ${run}: " + (Get-Content $log.FullName -Raw).Trim()
    Remove-Item $log.FullName
}

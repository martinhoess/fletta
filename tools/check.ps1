# Baut Fletta (Arbeitskopie = Ordner ueber tools) und fuehrt den Selbsttest aus. Exit-Code = Zahl der Fehler.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
dotnet build "$root\src\Fletta.csproj" -c Release --nologo -v m
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = "$root\src\bin\Release\net10.0-windows10.0.19041.0\win-x64\Fletta.exe"
$log = New-TemporaryFile
# Fletta ist eine GUI-Anwendung: ohne -Wait kaeme der Exit-Code nie an.
$p = Start-Process $exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow -RedirectStandardError $log.FullName
Get-Content $log.FullName
Remove-Item $log.FullName
"selftest exit: $($p.ExitCode)"
exit $p.ExitCode

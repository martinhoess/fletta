; Setup pro Benutzer ohne Adminrechte, Gegenstueck zu tools/install.ps1. Baut die CI (.github/workflows/release.yml):
;   ISCC.exe /DAppVersion=1.2.3 installer\fletta.iss
; Erwartet den self-contained Publish in ..\publish. Umlautfrei: Inno liest Dateien ohne BOM als ANSI.

#define AppName    "Fletta"
#define AppExeName AppName + ".exe"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Feste Kennung: bleibt, auch wenn sich der Name aendert - sonst gilt ein Update als zweites Programm.
AppId={{6D0B1E6C-2C4B-4D4E-9B7A-3F5E0C1A87D2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=martinhoess
AppPublisherURL=https://github.com/martinhoess/fletta
VersionInfoVersion={#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
; pdfium.dll gibt es nur fuer x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Laufende Fenster beendet der Restart Manager ueber die gesperrte EXE, beim Installieren wie beim Entfernen.
CloseApplications=force
RestartApplications=no
ChangesAssociations=yes
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\src\fletta.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.OpenDefaultApps=Open Default apps (set .pdf to {#AppName} there)
de.OpenDefaultApps=Standard-Apps oeffnen (dort .pdf auf {#AppName} stellen)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Mit pdfium.dll und licenses\ aus dem Publish.
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Anmeldung als PDF-Programm in HKCU (FileAssociation.cs). Den Standard setzt nur der Benutzer selbst.
Filename: "{app}\{#AppExeName}"; Parameters: "--register"; Flags: runhidden waituntilterminated
Filename: "ms-settings:defaultapps"; Description: "{cm:OpenDefaultApps}"; Flags: postinstall shellexec nowait skipifsilent
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--unregister"; RunOnceId: "UnregisterPdf"; Flags: runhidden waituntilterminated

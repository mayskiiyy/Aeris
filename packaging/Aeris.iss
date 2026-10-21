#ifndef AppVersion
  #error AppVersion must be passed by build.ps1
#endif

[Setup]
AppId={{76C5DAFA-7677-4E23-A965-4B341B5E4542}
AppName=Aeris
AppVersion={#AppVersion}
AppVerName=Aeris {#AppVersion}
AppPublisher=MAYSKIY
AppPublisherURL=https://github.com/mayskiiyy/Aeris
AppSupportURL=https://github.com/mayskiiyy/Aeris/issues
DefaultDirName={localappdata}\Programs\Aeris
DefaultGroupName=Aeris
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=Aeris-{#AppVersion}-Setup-x64
SetupIconFile=..\src\Aeris\Assets\aeris.ico
UninstallDisplayIcon={app}\Aeris.exe
UninstallDisplayName=Aeris
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
ShowLanguageDialog=auto
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\artifacts\Aeris-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Aeris"; Filename: "{app}\Aeris.exe"
Name: "{autodesktop}\Aeris"; Filename: "{app}\Aeris.exe"; Tasks: desktopicon

[Registry]
; Explorer menu entries are created by Aeris itself when the user turns them on. These lines only remove them on uninstall.
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Aeris.Scan"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Aeris.Duplicates"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Aeris.Largest"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Aeris.Scan"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Aeris.Duplicates"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Drive\shell\Aeris.Largest"; Flags: uninsdeletekey dontcreatekey

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Aeris automatic cleanup"" /F"; Flags: runhidden; RunOnceId: "DeleteScheduledCleanup"

[Run]
Filename: "{app}\Aeris.exe"; Description: "{cm:LaunchProgram,Aeris}"; Flags: nowait postinstall skipifsilent
; An update started from Aeris runs silently and passes /relaunch=1 so the new version opens again.
Filename: "{app}\Aeris.exe"; Flags: nowait; Check: ShouldRelaunch

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:relaunch|0}') = '1');
end;

#define AppName "Wavy"
#define AppVersion "1.9.3"
#define AppExe "Wavy.exe"
#define AppPublisher "csgd"

[Setup]
AppId={{8F3C1A62-5D74-4E1B-9A0F-2C6B7D8E4F51}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=no
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\dist
OutputBaseFilename=Wavy-{#AppVersion}-setup
SetupIconFile=..\src\Wavy\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern dynamic windows11 hidebevels
WizardBackColor=#F5F5F5
WizardBackColorDynamicDark=#101013
WizardSmallImageFile=..\src\Wavy\Assets\app.png
WizardSmallImageBackColor=#F5F5F5
WizardSmallImageBackColorDynamicDark=#101013
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

Filename: "{app}\{#AppExe}"; Flags: nowait; Check: IsWavyUpdate

[Code]
function IsWavyUpdate: Boolean;
begin
  Result := ExpandConstant('{param:WAVYUPDATE|0}') = '1';
end;

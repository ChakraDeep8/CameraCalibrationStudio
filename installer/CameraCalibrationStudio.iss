; Inno Setup script — builds a single one-click Setup.exe from the self-contained
; `dotnet publish` output, replacing a manual "download the zip, extract it yourself"
; distribution with a normal Windows installer (Start Menu shortcut, optional desktop
; icon, proper uninstaller, versioned upgrades).
;
; Build order:
;   1. dotnet publish ..\CameraCalibrationStudio -c Release -r win-x64 --self-contained true -o ..\publish
;   2. "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" CameraCalibrationStudio.iss
;   Output lands in ..\dist\CameraCalibrationStudio-Setup-<version>.exe
;
; Both steps are wrapped by installer\build-installer.ps1 — run that instead of doing
; them by hand.

#define MyAppName "Camera Calibration & Image Studio"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "ChakraDeep8"
#define MyAppURL "https://github.com/ChakraDeep8/CameraCalibrationStudio"
#define MyAppExeName "CameraCalibrationStudio.exe"

[Setup]
; Fixed GUID — keep this the same across versions so Windows treats upgrades as
; upgrades (proper "modify/repair/uninstall" behavior) rather than a second install.
AppId={{76225078-3194-4CA3-8877-DF26C585323F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Lets the installer run per-user with no admin prompt, or elevate — the person
; running it picks ("dialog"), which is what makes this genuinely one-click for
; someone without admin rights instead of demanding elevation up front.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=CameraCalibrationStudio-Setup-{#MyAppVersion}
SetupIconFile=..\CameraCalibrationStudio\Assets\logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The .NET self-contained payload already has everything it needs (no .NET install
; prompt), so this is a genuine single-.exe, no-prerequisites installer.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything dotnet publish produced — the exe, its dependencies, and the
; self-contained .NET runtime — goes in verbatim.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; The Data Builder is the same binary launched straight into that workspace, so the labelling
; workflow gets its own Start Menu entry without a second application to build and ship.
Name: "{group}\Data Builder"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--data-builder"; Comment: "Label frames and export a training set"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; No [UninstallDelete] for %AppData%\CameraCalibrationStudio on purpose — the class
; library there is documented to persist independently of any one install/calibration
; file (see README's "Notes" section), so uninstalling the app should not silently
; wipe it; a reinstall then picks the same classes back up.

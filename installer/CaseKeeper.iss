; Inno Setup script for CaseKeeper.
; Build it with:  powershell -File build-release.ps1
; (or compile this file directly once CaseKeeper has been published to ..\publish)

#define AppName        "CaseKeeper"
; Publisher brand only - no personal names anywhere in the installer
#define AppPublisher   "DarkCipher Systems"
; The folder under Program Files that holds this and any future apps
#define Publisher      "DarkCipher Systems"
; Where v0.1 – v0.2 installed. An upgrade from there moves to the new folder and removes the old
; program files (never the user's data, which lives in %LOCALAPPDATA%\CaseKeeper).
#define OldPublisher   "DarkCipher0x Systems"
#define AppExeName     "CaseKeeper.exe"
#define SourceDir      "..\publish"
; The version shown everywhere comes from the built exe, so it can never disagree with the app
#define AppVersion     GetStringFileInfo(SourceDir + "\" + AppExeName, "ProductVersion")

[Setup]
; This GUID identifies the application to Windows. NEVER change it: it is what lets v0.2
; recognise and upgrade an existing v0.1 instead of installing a second copy alongside it.
AppId={{B6E4A1D7-3F28-4C5B-9A07-5E1C8D42F093}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
; Installs to  C:\Program Files\DarkCipher Systems\CaseKeeper  (see [Code] for upgrades from the old folder)
DefaultDirName={autopf}\{#Publisher}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=CaseKeeper-Setup-v{#AppVersion}
SetupIconFile=..\CaseKeeper\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} v{#AppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LicenseFile=..\LICENSE
InfoBeforeFile=before-install.txt

; 64-bit only, and Windows 10 1809 or newer - the app uses the text recognition and
; PDF rendering built into Windows, which older versions don't have.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; Offer a per-user install to anyone without administrator rights
PrivilegesRequired=admin
; "dialog" lets a user without admin rights choose a per-user install;
; "commandline" allows /CURRENTUSER for unattended deployment and testing.
PrivilegesRequiredOverridesAllowed=dialog commandline

; Lets an upgrade replace files even if CaseKeeper is currently running
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a shortcut on the desktop"; GroupDescription: "Shortcuts:"

[Files]
; Never ship debug symbols or stray build files
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml,*.log"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\USER-GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\What's new in CaseKeeper"; Filename: "{app}\CHANGELOG.md"
Name: "{group}\CaseKeeper user guide"; Filename: "{app}\USER-GUIDE.md"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Open CaseKeeper now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only the program folder goes. Documents, notes and settings live in %LOCALAPPDATA%\CaseKeeper
; and are deliberately left alone, so reinstalling picks up where you left off.
Type: filesandordirs; Name: "{app}"

[Code]
var
  OldAppDir: String;

{ The program folder an earlier version used, if it was the old default location. }
function OldDefaultDir(): String;
begin
  Result := ExpandConstant('{autopf}\{#OldPublisher}\{#AppName}');
end;

{ An upgrade normally reinstalls into the previous folder. If that was the old default
  (DarkCipher0x Systems), switch to the new one instead. A folder someone chose themselves is kept.
  Runs for silent installs too. }
procedure InitializeWizard();
var
  Current: String;
begin
  OldAppDir := '';
  Current := RemoveBackslashUnlessRoot(WizardForm.DirEdit.Text);
  if CompareText(Current, OldDefaultDir()) = 0 then
  begin
    OldAppDir := Current;
    WizardForm.DirEdit.Text := ExpandConstant('{autopf}\{#Publisher}\{#AppName}');
    Log('Upgrade from the old folder ' + OldAppDir + ' - installing to ' + WizardForm.DirEdit.Text);
  end
  else if DirExists(OldDefaultDir()) and (CompareText(Current, OldDefaultDir()) <> 0) then
    // An old copy sitting in the old folder that isn't the registered install (e.g. a leftover)
    OldAppDir := OldDefaultDir();
end;

{ Once the new version is in place, remove the old program folder and the empty publisher folder. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  OldParent: String;
begin
  if (CurStep <> ssPostInstall) or (OldAppDir = '') then Exit;
  if CompareText(OldAppDir, RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0 then Exit;
  if not DirExists(OldAppDir) then Exit;

  // Ask a copy still running from the old folder to close (CaseKeeper saves as it goes)
  if FileExists(OldAppDir + '\{#AppExeName}') then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);

  if DelTree(OldAppDir, True, True, True) then
    Log('Removed the old program folder ' + OldAppDir)
  else
  begin
    Log('Could not fully remove ' + OldAppDir);
    if not WizardSilent() then
      MsgBox('CaseKeeper has moved to:' + #13#10 + ExpandConstant('{app}') + #13#10 + #13#10 +
             'The old program folder could not be removed completely (a file may be in use):' + #13#10 +
             OldAppDir + #13#10 + #13#10 +
             'You can delete it yourself. It contains only the old program, not your documents.',
             mbInformation, MB_OK);
  end;

  // The publisher folder itself goes too, but only if nothing else is in it
  OldParent := ExtractFileDir(OldAppDir);
  RemoveDir(OldParent);
end;

var
  DataRemoved: Boolean;

{ Before the program files go: offer to delete the library as well. The default answer is No, and
  a silent uninstall never deletes anything. Saying Yes runs CaseKeeper's own "Remove all CaseKeeper
  data" confirmation (as the signed-in user, not as administrator, so it finds that person's
  library), which lists exactly what will be deleted and needs DELETE typed. Exit code 0 means it
  was deleted. }
procedure OfferToRemoveData();
var
  ResultCode: Integer;
  Exe: String;
begin
  DataRemoved := False;
  Exe := ExpandConstant('{app}\{#AppExeName}');
  if not FileExists(Exe) then Exit;
  if SuppressibleMsgBox('Do you also want to delete your CaseKeeper library?' + #13#10 + #13#10 +
       'That is your documents, notes, timeline, complaints, exports and settings.' + #13#10 + #13#10 +
       'Choose No to keep them (recommended if you might reinstall: CaseKeeper picks up where you left off).' + #13#10 +
       'Choose Yes to see exactly what would be deleted and confirm it.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then Exit;
  if ExecAsOriginalUser(Exe, '--remove-data', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
    DataRemoved := (ResultCode = 0)
  else
    Log('Could not start CaseKeeper to remove its data: ' + SysErrorMessage(ResultCode));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // UninstallSilent guard: without it a /VERYSILENT uninstall waits forever on these dialogs
  if UninstallSilent then Exit;
  if CurUninstallStep = usUninstall then
    OfferToRemoveData()
  else if CurUninstallStep = usPostUninstall then
  begin
    if DataRemoved then
      MsgBox('CaseKeeper and your CaseKeeper data have been removed from this PC.' + #13#10 + #13#10 +
             'Backups you saved elsewhere, and the original files you imported from, have not been touched.',
             mbInformation, MB_OK)
    else
      MsgBox('CaseKeeper has been removed.' + #13#10 + #13#10 +
             'Your documents, notes, timeline and settings have been left on this PC at:' + #13#10 +
             ExpandConstant('{localappdata}\CaseKeeper') + #13#10 + #13#10 +
             '(If you moved your library somewhere else, it is still there.)' + #13#10 +
             'To remove them later, reinstall CaseKeeper and use Settings > Factory reset > Remove all CaseKeeper data,' + #13#10 +
             'or delete that folder yourself.',
             mbInformation, MB_OK);
  end;
end;

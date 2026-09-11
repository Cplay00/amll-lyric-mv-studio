#define MyAppName "AMLL MV Studio"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MyAppFlavor
  #define MyAppFlavor "Full"
#endif
#if MyAppFlavor == "Lite"
  #define MyAppSetupName "AMLL-MV-Studio-" + MyAppVersion + "-x64-Lite-Setup"
#else
  #define MyAppSetupName "AMLL-MV-Studio-" + MyAppVersion + "-x64-Setup"
#endif
#define MyAppPublisher "AMLL MV Studio contributors"
#define MyAppExeName "AmllMvStudio.exe"
#define MyAppId "{{9A522AA8-C6AB-4CF0-B743-62F1BE0D24B1}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} ({#MyAppFlavor})
AppPublisher={#MyAppPublisher}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={localappdata}\Programs\AMLL MV Studio
DefaultGroupName=AMLL MV Studio
DisableProgramGroupPage=auto
PrivilegesRequired=lowest
UsedUserAreasWarning=no
Uninstallable=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=output
OutputBaseFilename={#MyAppSetupName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardResizable=yes
ChangesEnvironment=no
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
MinVersion=10.0.17763
LicenseFile=LICENSE.txt
InfoBeforeFile=NOTICE.txt
InfoAfterFile=THIRD-PARTY-NOTICES.md
SetupIconFile=staging\Assets\app.ico
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} per-user installer
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenu"; Description: "{cm:CreateStartMenuShortcut}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\runtime\webview2\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy noencryption

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: startmenu
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "{cm:InstallingWebView2}"; Flags: waituntilterminated; Check: WebView2InstallRequired
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[InstallDelete]
#if MyAppFlavor == "Lite"
; Switching from Full to Lite removes the embedded browser directory.
Type: filesandordirs; Name: "{app}\runtime\browsers"
#endif

[UninstallDelete]
; Only per-user generated logs and an empty data directory are removed. The
; WebView2 profile and other application data are intentionally preserved.
; User-selected rendered videos and arbitrary source media are never deleted.
Type: files; Name: "{localappdata}\AmllMvStudio\startup-error.log"
Type: dirifempty; Name: "{localappdata}\AmllMvStudio"

[CustomMessages]
english.CreateStartMenuShortcut=Create a Start Menu shortcut
english.InstallingWebView2=Installing Microsoft Edge WebView2 Runtime...

[Code]
const
  WebView2ClientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2VersionFromRoot(const RootKey: Integer): String;
var
  Key: String;
begin
  Key := 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2ClientId;
  if not RegQueryStringValue(RootKey, Key, 'pv', Result) then
    Result := '';
end;

function WebView2InstallRequired: Boolean;
var
  Version: String;
begin
  Version := WebView2VersionFromRoot(HKLM64);
  if Version = '' then Version := WebView2VersionFromRoot(HKLM32);
  if Version = '' then Version := WebView2VersionFromRoot(HKCU);
  Result := (Version = '') or (Version = '0.0.0.0');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  BrowserDir: String;
begin
  if CurStep = ssPostInstall then begin
    if '{#MyAppFlavor}' <> 'Lite' then begin
      BrowserDir := ExpandConstant('{app}\runtime\browsers');
      { Set a per-user environment value for child processes and future GUI probing. }
      { The application shortcut still launches the GUI executable directly. }
      RegWriteStringValue(HKCU, 'Environment', 'PLAYWRIGHT_BROWSERS_PATH', BrowserDir);
    end;

    if WebView2InstallRequired then
      ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  BrowserDir: String;
  CurrentValue: String;
begin
  if CurUninstallStep = usUninstall then begin
    BrowserDir := ExpandConstant('{app}\runtime\browsers');
    if RegQueryStringValue(HKCU, 'Environment', 'PLAYWRIGHT_BROWSERS_PATH', CurrentValue) and
       (CompareText(CurrentValue, BrowserDir) = 0) then
      RegDeleteValue(HKCU, 'Environment', 'PLAYWRIGHT_BROWSERS_PATH');
  end;
end;

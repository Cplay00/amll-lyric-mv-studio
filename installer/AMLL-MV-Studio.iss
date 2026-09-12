#define MyAppName "AMLL Lyric MV Studio"
#ifndef MyAppVersion
  #define MyAppVersion "0.2.1"
#endif
#ifndef MyAppFlavor
  #define MyAppFlavor "Full"
#endif
#if MyAppFlavor == "Lite"
  #define MyAppSetupName "AMLL-Lyric-MV-Studio-" + MyAppVersion + "-x64-Lite-Setup"
#else
  #define MyAppSetupName "AMLL-Lyric-MV-Studio-" + MyAppVersion + "-x64-Setup"
#endif
#define MyAppPublisher "Cplay00 and AMLL Lyric MV Studio contributors"
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
DefaultDirName={localappdata}\Programs\AMLL Lyric MV Studio
DefaultGroupName=AMLL Lyric MV Studio
DisableProgramGroupPage=auto
DisableWelcomePage=no
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
; 语言机制（Inno 6.7.3 实测）：uilanguage + auto 是唯一同时满足以下三点的组合——
; ① 无 /LANG 时按系统 UI 语言选默认（中文系统 → zhcn，无原生对话框）；
; ② ShowLanguageDialog=no 会静默忽略 /LANG，导致欢迎页语言切换的重启流程失效；
; ③ LanguageDetectionMethod=none 时 auto 判定"UI 语言无匹配"而每次都弹原生对话框，
;    且对话框预选与 /LANG 无关（会覆盖重启流程选定的语言）。
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto
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
; zhcn 置首仅作兜底：LanguageDetectionMethod=uilanguage 下无匹配 UI 语言时才落到首项。
Name: "zhcn"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "startmenu"; Description: "{cm:CreateStartMenuShortcut}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "staging\runtime\webview2\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy noencryption

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: startmenu
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
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
zhcn.CreateStartMenuShortcut=创建“开始”菜单快捷方式
zhcn.InstallingWebView2=正在安装 Microsoft Edge WebView2 Runtime...
zhcn.WebView2LaunchFailed=无法启动 Microsoft Edge WebView2 Runtime 安装程序：%1
zhcn.WebView2InstallFailed=Microsoft Edge WebView2 Runtime 安装失败（退出代码 %1）。
zhcn.WebView2RestartRequired=Microsoft Edge WebView2 Runtime 安装需要重启 Windows。重启后请再次运行安装程序。
zhcn.WelcomeTitle=欢迎使用 AMLL Lyric MV Studio
zhcn.WelcomeText=将音频、逐字歌词与专辑封面制作成 Apple Music 风格歌词 MV。%n%n安装程序将安装 {#MyAppFlavor} 版 {#MyAppVersion}，之后可从“开始”菜单启动。
zhcn.LanguageLabel=安装语言：
zhcn.LanguageSelectRequired=请先选择安装语言。
zhcn.LanguageSwitchGuard=已尝试过语言切换，将继续使用当前语言。
zhcn.LanguageSwitchFailed=无法以所选语言重启安装程序。
english.CreateStartMenuShortcut=Create a Start Menu shortcut
english.InstallingWebView2=Installing Microsoft Edge WebView2 Runtime...
english.WebView2LaunchFailed=Unable to start the Microsoft Edge WebView2 Runtime installer: %1
english.WebView2InstallFailed=Microsoft Edge WebView2 Runtime installation failed (exit code %1).
english.WebView2RestartRequired=Microsoft Edge WebView2 Runtime installation requires a Windows restart. Restart Windows, then run Setup again.
english.WelcomeTitle=Welcome to AMLL Lyric MV Studio
english.WelcomeText=Create Apple Music-style lyric videos from audio, timed lyrics, and album artwork.%n%nSetup will install the {#MyAppFlavor} edition, version {#MyAppVersion}.
english.LanguageLabel=Installer language:
english.LanguageSelectRequired=Select an installer language before continuing.
english.LanguageSwitchGuard=Language switching was already attempted. Continue with the current language.
english.LanguageSwitchFailed=Unable to restart Setup in the selected language.
japanese.CreateStartMenuShortcut=スタート メニューのショートカットを作成する
japanese.InstallingWebView2=Microsoft Edge WebView2 Runtime をインストールしています...
japanese.WebView2LaunchFailed=Microsoft Edge WebView2 Runtime インストーラーを起動できませんでした: %1
japanese.WebView2InstallFailed=Microsoft Edge WebView2 Runtime のインストールに失敗しました (終了コード %1)。
japanese.WebView2RestartRequired=Microsoft Edge WebView2 Runtime のインストールには Windows の再起動が必要です。再起動後、セットアップをもう一度実行してください。
japanese.WelcomeTitle=AMLL Lyric MV Studio へようこそ
japanese.WelcomeText=オーディオ、タイミング付き歌詞、アルバムアートから Apple Music スタイルの歌詞動画を作成します。%n%nセットアップは {#MyAppFlavor} エディション、バージョン {#MyAppVersion} をインストールします。
japanese.LanguageLabel=インストーラーの言語:
japanese.LanguageSelectRequired=続行する前にインストーラーの言語を選択してください。
japanese.LanguageSwitchGuard=言語の切り替えは既に試行されました。現在の言語で続行します。
japanese.LanguageSwitchFailed=選択した言語でセットアップを再起動できませんでした。
korean.CreateStartMenuShortcut=시작 메뉴 바로 가기 만들기
korean.InstallingWebView2=Microsoft Edge WebView2 Runtime 설치 중...
korean.WebView2LaunchFailed=Microsoft Edge WebView2 Runtime 설치 프로그램을 시작할 수 없습니다: %1
korean.WebView2InstallFailed=Microsoft Edge WebView2 Runtime 설치에 실패했습니다(종료 코드 %1).
korean.WebView2RestartRequired=Microsoft Edge WebView2 Runtime 설치를 완료하려면 Windows를 다시 시작해야 합니다. 다시 시작한 후 설치 프로그램을 다시 실행하십시오.
korean.WelcomeTitle=AMLL Lyric MV Studio에 오신 것을 환영합니다
korean.WelcomeText=오디오, 타이밍이 지정된 가사 및 앨범 아트로 Apple Music 스타일의 가사 비디오를 만듭니다.%n%n설치 프로그램이 {#MyAppFlavor} 에디션 버전 {#MyAppVersion}을(를) 설치합니다.
korean.LanguageLabel=설치 언어:
korean.LanguageSelectRequired=계속하기 전에 설치 언어를 선택하십시오.
korean.LanguageSwitchGuard=언어 전환이 이미 시도되었습니다. 현재 언어로 계속합니다.
korean.LanguageSwitchFailed=선택한 언어로 설치 프로그램을 다시 시작할 수 없습니다.
french.CreateStartMenuShortcut=Créer un raccourci dans le menu Démarrer
french.InstallingWebView2=Installation du runtime Microsoft Edge WebView2...
french.WebView2LaunchFailed=Impossible de démarrer le programme d'installation du runtime Microsoft Edge WebView2 : %1
french.WebView2InstallFailed=L'installation du runtime Microsoft Edge WebView2 a échoué (code de sortie %1).
french.WebView2RestartRequired=L'installation du runtime Microsoft Edge WebView2 nécessite un redémarrage de Windows. Redémarrez Windows, puis relancez le programme d'installation.
french.WelcomeTitle=Bienvenue dans AMLL Lyric MV Studio
french.WelcomeText=Créez des vidéos de paroles de style Apple Music à partir d'audio, de paroles synchronisées et d'illustrations d'album.%n%nLe programme d'installation installera l'édition {#MyAppFlavor}, version {#MyAppVersion}.
french.LanguageLabel=Langue d'installation :
french.LanguageSelectRequired=Sélectionnez une langue d'installation avant de continuer.
french.LanguageSwitchGuard=Le changement de langue a déjà été tenté. Poursuivez avec la langue actuelle.
french.LanguageSwitchFailed=Impossible de redémarrer l'installation dans la langue sélectionnée.
german.CreateStartMenuShortcut=Startmenü-Verknüpfung erstellen
german.InstallingWebView2=Microsoft Edge WebView2 Runtime wird installiert...
german.WebView2LaunchFailed=Das Installationsprogramm für Microsoft Edge WebView2 Runtime konnte nicht gestartet werden: %1
german.WebView2InstallFailed=Die Installation von Microsoft Edge WebView2 Runtime ist fehlgeschlagen (Beendigungscode %1).
german.WebView2RestartRequired=Die Installation von Microsoft Edge WebView2 Runtime erfordert einen Windows-Neustart. Starten Sie Windows neu und führen Sie Setup anschließend erneut aus.
german.WelcomeTitle=Willkommen bei AMLL Lyric MV Studio
german.WelcomeText=Erstellen Sie Liedtextvideos im Apple-Music-Stil aus Audio, zeitgesteuerten Liedtexten und Albumcovern.%n%nSetup installiert die {#MyAppFlavor}-Edition, Version {#MyAppVersion}.
german.LanguageLabel=Installationssprache:
german.LanguageSelectRequired=Wählen Sie vor dem Fortfahren eine Installationssprache.
german.LanguageSwitchGuard=Der Sprachwechsel wurde bereits versucht. Es wird mit der aktuellen Sprache fortgefahren.
german.LanguageSwitchFailed=Das Setup konnte in der ausgewählten Sprache nicht neu gestartet werden.
spanish.CreateStartMenuShortcut=Crear un acceso directo en el menú Inicio
spanish.InstallingWebView2=Instalando Microsoft Edge WebView2 Runtime...
spanish.WebView2LaunchFailed=No se pudo iniciar el instalador de Microsoft Edge WebView2 Runtime: %1
spanish.WebView2InstallFailed=La instalación de Microsoft Edge WebView2 Runtime falló (código de salida %1).
spanish.WebView2RestartRequired=La instalación de Microsoft Edge WebView2 Runtime requiere reiniciar Windows. Reinicie Windows y vuelva a ejecutar el programa de instalación.
spanish.WelcomeTitle=Bienvenido a AMLL Lyric MV Studio
spanish.WelcomeText=Cree vídeos de letras al estilo de Apple Music a partir de audio, letras sincronizadas y carátulas de álbumes.%n%nEl programa de instalación instalará la edición {#MyAppFlavor}, versión {#MyAppVersion}.
spanish.LanguageLabel=Idioma de instalación:
spanish.LanguageSelectRequired=Seleccione un idioma de instalación antes de continuar.
spanish.LanguageSwitchGuard=El cambio de idioma ya se intentó. Continúe con el idioma actual.
spanish.LanguageSwitchFailed=No se pudo reiniciar la instalación en el idioma seleccionado.
russian.CreateStartMenuShortcut=Создать ярлык в меню «Пуск»
russian.InstallingWebView2=Установка Microsoft Edge WebView2 Runtime...
russian.WebView2LaunchFailed=Не удалось запустить установщик Microsoft Edge WebView2 Runtime: %1
russian.WebView2InstallFailed=Не удалось установить Microsoft Edge WebView2 Runtime (код выхода %1).
russian.WebView2RestartRequired=Для установки Microsoft Edge WebView2 Runtime требуется перезапуск Windows. Перезапустите Windows, затем снова запустите программу установки.
russian.WelcomeTitle=Добро пожаловать в AMLL Lyric MV Studio
russian.WelcomeText=Создавайте видео с текстами песен в стиле Apple Music из аудио, синхронизированных текстов и обложек альбомов.%n%nПрограмма установки установит выпуск {#MyAppFlavor}, версия {#MyAppVersion}.
russian.LanguageLabel=Язык установки:
russian.LanguageSelectRequired=Выберите язык установки перед продолжением.
russian.LanguageSwitchGuard=Смена языка уже выполнялась. Продолжение на текущем языке.
russian.LanguageSwitchFailed=Не удалось перезапустить программу установки на выбранном языке.

[Code]
const
  WebView2ClientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  LanguageRestartGuardName = 'AMLLLANGRESTARTED';
  LanguageRestartGuard = '/' + LanguageRestartGuardName + '=1';

var
  LanguageLabel: TNewStaticText;
  LanguageCombo: TNewComboBox;
  LanguageIds: TStringList;
  LanguageRestarting: Boolean;

procedure AddLanguage(const DisplayName, LanguageId: String);
begin
  LanguageCombo.Items.Add(DisplayName);
  LanguageIds.Add(LanguageId);
end;

procedure InitializeLanguageSelector;
var
  CurrentIndex: Integer;
begin
  LanguageIds := TStringList.Create;

  LanguageLabel := TNewStaticText.Create(WizardForm);
  LanguageLabel.Parent := WizardForm.WelcomePage;
  LanguageLabel.AutoSize := True;
  LanguageLabel.Anchors := [akLeft, akBottom];
  LanguageLabel.Left := WizardForm.WelcomeLabel2.Left;
  LanguageLabel.Top := WizardForm.WelcomePage.ClientHeight - ScaleY(30);
  LanguageLabel.Caption := CustomMessage('LanguageLabel');

  // 语言选择器位于内容区底部、与“下一步/取消”同一水平行的左侧：
  // 不再压在左侧插图栏上（旧实现 Left=ScaleX(8) 恰好叠在欢迎图内），并修正标签与下拉的基线对齐。
  LanguageCombo := TNewComboBox.Create(WizardForm);
  LanguageCombo.Parent := WizardForm.WelcomePage;
  LanguageCombo.Style := csDropDownList;
  LanguageCombo.Left := LanguageLabel.Left + LanguageLabel.Width + ScaleX(8);
  LanguageCombo.Top := WizardForm.WelcomePage.ClientHeight - ScaleY(34);
  LanguageCombo.Width := ScaleX(160);
  LanguageCombo.Anchors := [akLeft, akBottom];

  if LanguageCombo.Height > LanguageLabel.Height then
    LanguageLabel.Top := LanguageCombo.Top + ((LanguageCombo.Height - LanguageLabel.Height) div 2);

  AddLanguage('简体中文', 'zhcn');
  AddLanguage('English', 'english');
  AddLanguage('日本語', 'japanese');
  AddLanguage('한국어', 'korean');
  AddLanguage('Français', 'french');
  AddLanguage('Deutsch', 'german');
  AddLanguage('Español', 'spanish');
  AddLanguage('Русский', 'russian');
  CurrentIndex := LanguageIds.IndexOf(ActiveLanguage);
  if CurrentIndex < 0 then CurrentIndex := 0;
  LanguageCombo.ItemIndex := CurrentIndex;
end;

procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel1.Caption := CustomMessage('WelcomeTitle');
  WizardForm.WelcomeLabel2.Caption := CustomMessage('WelcomeText');
  InitializeLanguageSelector;
end;

function StartsWithText(const Prefix, Value: String): Boolean;
begin
  Result := CompareText(Copy(Value, 1, Length(Prefix)), Prefix) = 0;
end;

function IsLanguageParameter(const Parameter: String): Boolean;
begin
  Result := StartsWithText('/LANG=', Parameter) or
    StartsWithText('-LANG=', Parameter);
end;

function IsLanguageRestartGuardParameter(const Parameter: String): Boolean;
var
  Prefix: String;
begin
  Prefix := '/' + LanguageRestartGuardName;
  Result := (CompareText(Parameter, Prefix) = 0) or
    StartsWithText(Prefix + '=', Parameter) or
    (CompareText(Parameter, '-' + LanguageRestartGuardName) = 0) or
    StartsWithText('-' + LanguageRestartGuardName + '=', Parameter);
end;

function QuoteCommandLineParameter(const Parameter: String): String;
var
  Index: Integer;
  BackslashCount: Integer;
begin
  if (Parameter <> '') and
     (Pos(' ', Parameter) = 0) and
     (Pos(#9, Parameter) = 0) and
     (Pos('"', Parameter) = 0) then begin
    Result := Parameter;
    Exit;
  end;

  Result := '"';
  BackslashCount := 0;
  for Index := 1 to Length(Parameter) do begin
    if Parameter[Index] = '\' then
      BackslashCount := BackslashCount + 1
    else begin
      if Parameter[Index] = '"' then
        Result := Result + StringOfChar('\', BackslashCount * 2 + 1)
      else
        Result := Result + StringOfChar('\', BackslashCount);
      BackslashCount := 0;
      Result := Result + Parameter[Index];
    end;
  end;
  Result := Result + StringOfChar('\', BackslashCount * 2) + '"';
end;

function LanguageRestartParameters(const SelectedLanguage: String): String;
var
  Index: Integer;
  Parameter: String;
begin
  Result := '';
  for Index := 1 to ParamCount do begin
    Parameter := ParamStr(Index);
    if (not IsLanguageParameter(Parameter)) and
       (not IsLanguageRestartGuardParameter(Parameter)) then
      Result := Result + QuoteCommandLineParameter(Parameter) + ' ';
  end;
  Result := Result + '/LANG=' + SelectedLanguage + ' ' + LanguageRestartGuard;
end;

function GetCurrentProcessId: Cardinal;
external 'GetCurrentProcessId@kernel32.dll stdcall';

function PSQuote(const S: String): String;
begin
  { srcexe 与重启参数不含单引号，直接包裹即可 }
  Result := '''' + S + '''';
end;

function HasLanguageRestartGuard: Boolean;
var
  Index: Integer;
  Parameter: String;
  Eq: Integer;
begin
  { 兼容 /NAME=1、-NAME=1 与裸 NAME=1：经 PowerShell Start-Process 转发时
    第二个参数的前导斜杠会被剥掉（PowerShell 5.1 实测），内置 param 常量不识别裸形式。 }
  Result := False;
  for Index := 1 to ParamCount do begin
    Parameter := ParamStr(Index);
    if StartsWithText(LanguageRestartGuardName + '=', Parameter) or
       StartsWithText('/' + LanguageRestartGuardName + '=', Parameter) or
       StartsWithText('-' + LanguageRestartGuardName + '=', Parameter) then begin
      Eq := Pos('=', Parameter);
      Result := Copy(Parameter, Eq + 1, 1) = '1';
      if Result then Exit;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedLanguage: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := True;
  if CurPageID <> wpWelcome then Exit;
  if LanguageCombo.ItemIndex < 0 then begin
    MsgBox(CustomMessage('LanguageSelectRequired'), mbError, MB_OK);
    Result := False;
    Exit;
  end;
  SelectedLanguage := LanguageIds[LanguageCombo.ItemIndex];
  if CompareText(SelectedLanguage, ActiveLanguage) = 0 then Exit;

  if HasLanguageRestartGuard then begin
    MsgBox(CustomMessage('LanguageSwitchGuard'), mbInformation, MB_OK);
    Exit;
  end;

  Parameters := LanguageRestartParameters(SelectedLanguage);
  if ShellExec('', ExpandConstant('{srcexe}'), Parameters, '', SW_SHOWNORMAL, ewNoWait, ResultCode) then begin
    LanguageRestarting := True;
    WizardForm.Close;
    Result := False;
  end else if ShellExec('', 'powershell.exe',
      '-NoProfile -WindowStyle Hidden -Command "Wait-Process -Id ' +
      IntToStr(GetCurrentProcessId()) + ' -ErrorAction SilentlyContinue; ' +
      'Start-Process -FilePath ' + PSQuote(ExpandConstant('{srcexe}')) +
      ' -ArgumentList ' + PSQuote(Parameters) + '"',
      '', SW_HIDE, ewNoWait, ResultCode) then begin
    { 后备路径：部分环境下（loader/engine 持有 srcexe 句柄）直接自启会
      返回 SE_ERR_ACCESSDENIED，改为等本实例退出后再由 PowerShell 拉起。 }
    LanguageRestarting := True;
    WizardForm.Close;
    Result := False;
  end else
    MsgBox(CustomMessage('LanguageSwitchFailed'), mbError, MB_OK);
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if LanguageRestarting then Confirm := False;
end;

procedure DeinitializeSetup;
begin
  if LanguageIds <> nil then
    LanguageIds.Free;
end;

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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstallerPath: String;
  ResultCode: Integer;
begin
  Result := '';
  if not WebView2InstallRequired then Exit;

  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  InstallerPath := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
  if not Exec(InstallerPath, '/silent /install', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then begin
    Result := FmtMessage(CustomMessage('WebView2LaunchFailed'), [SysErrorMessage(ResultCode)]);
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then begin
    Result := FmtMessage(CustomMessage('WebView2InstallFailed'), [IntToStr(ResultCode)]);
    Exit;
  end;

  if ResultCode = 3010 then begin
    NeedsRestart := True;
    Result := CustomMessage('WebView2RestartRequired');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  BrowserDir: String;
  CurrentValue: String;
begin
  if CurStep = ssPostInstall then begin
    BrowserDir := ExpandConstant('{app}\runtime\browsers');
    if '{#MyAppFlavor}' <> 'Lite' then begin
      { Set a per-user environment value for child processes and future GUI probing. }
      { The application shortcut still launches the GUI executable directly. }
      RegWriteStringValue(HKCU, 'Environment', 'PLAYWRIGHT_BROWSERS_PATH', BrowserDir);
    end else if
      RegQueryStringValue(HKCU, 'Environment', 'PLAYWRIGHT_BROWSERS_PATH', CurrentValue) and
      (CompareText(CurrentValue, BrowserDir) = 0) then
      RegDeleteValue(HKCU, 'Environment', 'PLAYWRIGHT_BROWSERS_PATH');
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

; e-note Windows Setup Script (Inno Setup 6+)
; Packages the complete self-contained win-x64 build.
; Supports custom install directory, desktop shortcut, and launch at Windows login.

#define MyAppName "e-note"
#define MyAppEnglishName "e-note"
#define MyAppVersion "v1.0"
#define MyAppPublisher "e-note Authors"
#define MyAppCopyright "Copyright (C) 2026 e-note Authors"
#define MyAppExeName "DesktopNotes.Desktop.exe"
#define MyAppId "{9F47E810-3C1A-4D25-9B82-6E40A11D29C0}"
#define MyAppUserModelId "DesktopNotes.StickyNotes.V1"

[Setup]
AppId={{#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright={#MyAppCopyright}
LicenseFile=..\..\LICENSE
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\..\
OutputBaseFilename=e-note-v1.0-Setup
SetupIconFile=..\..\src\DesktopNotes.Desktop\Assets\Icons\app-icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableDirPage=no
CloseApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName},0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加选项:"; Flags: checkedonce
Name: "launchatlogin"; Description: "开机自动启动 e-note (随 Windows 登录启动)(&L)"; GroupDescription: "附加选项:"; Flags: checkedonce

[Files]
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml,*Tests*,*.dbg"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\src\DesktopNotes.Desktop\Assets\Icons\app-icon.ico"; DestDir: "{app}"; DestName: "app-icon.ico"; Flags: ignoreversion
Source: "..\..\src\DesktopNotes.Desktop\Assets\Icons\app-icon.ico"; DestDir: "{app}\Assets\Icons"; Flags: ignoreversion
Source: "..\..\src\DesktopNotes.Desktop\Assets\Icons\tray-icon.png"; DestDir: "{app}\Assets\Icons"; Flags: ignoreversion

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; IconIndex: 0; AppUserModelID: "{#MyAppUserModelId}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; IconIndex: 0; AppUserModelID: "{#MyAppUserModelId}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DesktopNotes"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: launchatlogin; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 e-note"; Flags: nowait postinstall skipifsilent

[Code]
var
  AttachmentDirPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  AttachmentDirPage := CreateInputDirPage(
    wpSelectDir,
    '选择默认附件与数据存储位置',
    '请指定任务与笔记附件的存储位置（与程序安装目录完全分离）',
    '应用数据默认保存在 %LOCALAPPDATA%\DesktopNotes\data\app.db。' + #13#10 +
    '卸载或升级程序时，默认保留所有任务数据库与用户数据。',
    False,
    'DesktopNotesAttachments');
  AttachmentDirPage.Add('默认附件存储目录:');
  AttachmentDirPage.Values[0] := ExpandConstant('{userdocs}\DesktopNotesAttachments');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataRootDir: String;
  IntentFilePath: String;
  AppIntentFilePath: String;
  EscapedAttachmentDir: String;
  LaunchBoolStr: String;
  JsonContent: String;
begin
  if CurStep = ssPostInstall then
  begin
    DataRootDir := ExpandConstant('{localappdata}\DesktopNotes');
    ForceDirectories(DataRootDir);
    ForceDirectories(AttachmentDirPage.Values[0]);

    EscapedAttachmentDir := AttachmentDirPage.Values[0];
    StringChangeEx(EscapedAttachmentDir, '\', '\\', True);

    if WizardIsTaskSelected('launchatlogin') then
      LaunchBoolStr := 'true'
    else
      LaunchBoolStr := 'false';

    JsonContent :=
      '{' + #13#10 +
      '  "defaultAttachmentDirectory": "' + EscapedAttachmentDir + '",' + #13#10 +
      '  "launchAtLogin": ' + LaunchBoolStr + #13#10 +
      '}';

    IntentFilePath := DataRootDir + '\first-run-intent.json';
    SaveStringToFile(IntentFilePath, JsonContent, False);
    SaveStringToFile(DataRootDir + '\install-intent.json', JsonContent, False);

    AppIntentFilePath := ExpandConstant('{app}\first-run-intent.json');
    SaveStringToFile(AppIntentFilePath, JsonContent, False);
  end;
end;

function InitializeUninstall(): Boolean;
var
  ErrorCode: Integer;
begin
  // 卸载前强制退出正在运行的 e-note 主进程与后台托盘，释放所有文件锁
  Exec('taskkill.exe', '/F /T /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  Sleep(600);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataRootDir: String;
  AppDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 彻底清理安装目录残留文件与子文件夹
    AppDir := ExpandConstant('{app}');
    if DirExists(AppDir) then
    begin
      DelTree(AppDir, True, True, True);
    end;

    // 提示用户是否一并清除本地历史任务与便签数据
    DataRootDir := ExpandConstant('{localappdata}\DesktopNotes');
    if DirExists(DataRootDir) then
    begin
      if MsgBox('是否同时清理本地便签任务数据及用户配置？' + #13#10#13#10 +
                '数据位置: ' + DataRootDir + #13#10#13#10 +
                '【是】：彻底清空所有本地历史任务与便签数据' + #13#10 +
                '【否】：保留历史数据，以便重新安装时恢复',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataRootDir, True, True, True);
      end;
    end;
  end;
end;

; 电子便签 (Sticky Notes) Windows Setup Script (Inno Setup 6+)
; Packages the complete self-contained win-x64 build.
; Supports custom install directory, desktop shortcut, and launch at Windows login.

#define MyAppName "电子便签"
#define MyAppEnglishName "Sticky Notes"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "电子便签"
#define MyAppExeName "DesktopNotes.Desktop.exe"

[Setup]
AppId={{9F47E810-3C1A-4D25-9B82-6E40A11D29C0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\..\
OutputBaseFilename=电子便签-v1.0-Setup
SetupIconFile=..\..\src\DesktopNotes.Desktop\Assets\Icons\app-icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableDirPage=no
CloseApplications=yes
UninstallDisplayIcon={app}\Assets\Icons\app-icon.ico

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加选项:"; Flags: checkedonce
Name: "launchatlogin"; Description: "开机自动启动电子便签 (随 Windows 登录启动)(&L)"; GroupDescription: "附加选项:"; Flags: checkedonce

[Files]
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\Icons\app-icon.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\Icons\app-icon.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DesktopNotes"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: launchatlogin; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 电子便签"; Flags: nowait postinstall skipifsilent

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
  end;
end;

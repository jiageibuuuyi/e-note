; DesktopNotes Windows V1 User-Level Installer (Inno Setup 6+)
; Packages the complete self-contained output directory: artifacts/win-x64
; Keeps program installation directory separate from user database & attachment directories.
; Uninstall preserves user data and managed attachments by default.

#define MyAppName "DesktopNotes"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "DesktopNotes Project"
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
OutputDir=..\..\artifacts\installer
OutputBaseFilename=DesktopNotes-Setup-win-x64-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加图标:"
Name: "launchatlogin"; Description: "开机登录时自动启动 DesktopNotes (默认关闭)(&L)"; GroupDescription: "启动选项:"; Flags: unchecked

[Files]
Source: "..\..\artifacts\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code]
var
  AttachmentDirPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  AttachmentDirPage := CreateInputDirPage(
    wpSelectDir,
    '选择默认附件存储根目录',
    '请指定任务与笔记附件的默认存储位置（与程序安装目录及本地数据库目录分离）',
    '应用数据库默认保存在 %LOCALAPPDATA%\DesktopNotes\data\app.db。' + #13#10 +
    '卸载或升级程序时，默认保留所有任务数据库与受管附件目录。',
    False,
    'DesktopNotesAttachments');
  AttachmentDirPage.Add('默认附件根目录:');
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

#ifndef PayloadDir
  #error PayloadDir must point to a clean installer staging directory
#endif
#ifndef ArtifactDir
  #error ArtifactDir must be inside QuickCapture/artifacts
#endif
#ifndef AppVersion
  #error AppVersion must match the published application
#endif
#ifndef UninstallLauncher
  #error UninstallLauncher must point to the native uninsQuickCapture.exe
#endif

[Setup]
AppId={{9338503C-6532-41BE-B6B4-A417CF7DC204}
AppName=轻截 QuickCapture
AppVersion={#AppVersion}
AppVerName=轻截 QuickCapture {#AppVersion}
AppPublisher=Xhonesty
AppPublisherURL=https://github.com/Xhonesty/QuickCapture
AppSupportURL=https://github.com/Xhonesty/QuickCapture/issues
AppUpdatesURL=https://github.com/Xhonesty/QuickCapture/releases
DefaultDirName={localappdata}\Programs\QuickCapture
DefaultGroupName=轻截 QuickCapture
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupArchitecture=x64
MinVersion=10.0.19045
AppMutex=Local\QuickCapture.Personal.x64
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UninstallDisplayIcon={app}\QuickCapture.exe
UninstallDisplayName=轻截 QuickCapture
Uninstallable=yes
CreateUninstallRegKey=ShouldRegisterUninstall
OutputDir={#ArtifactDir}
OutputBaseFilename=QuickCapture-Setup-{#AppVersion}-x64
SetupIconFile=..\src\Assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
DisableWelcomePage=no
InfoBeforeFile=installation-info.txt
VersionInfoVersion={#AppVersion}
VersionInfoDescription=轻截 QuickCapture 安装程序
VersionInfoProductName=轻截 QuickCapture
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "chinesesimp"; MessagesFile: "languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; Check: ShouldCreateIcons

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "Data\*,Captures\*,Diagnostics\*,installed-mode,unins*"
Source: "installed-mode"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#UninstallLauncher}"; DestDir: "{app}"; Flags: ignoreversion

[INI]
Filename: "{app}\uninsQuickCapture.ini"; Section: "Uninstall"; Key: "Executable"; String: "{code:NativeUninstallerName}"

[UninstallDelete]
Type: files; Name: "{app}\uninsQuickCapture.ini"

[Icons]
Name: "{group}\轻截 QuickCapture"; Filename: "{app}\QuickCapture.exe"; WorkingDir: "{app}"; Check: ShouldCreateIcons
Name: "{group}\卸载轻截 QuickCapture"; Filename: "{app}\uninsQuickCapture.exe"; Check: ShouldCreateIcons
Name: "{autodesktop}\轻截 QuickCapture"; Filename: "{app}\QuickCapture.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: ShouldCreateIcons

[Run]
Filename: "{app}\QuickCapture.exe"; Description: "启动轻截 QuickCapture"; Flags: nowait postinstall skipifsilent unchecked

[Code]
function ValidationMode: Boolean;
begin
  Result := ExpandConstant('{param:VALIDATION|0}') = '1';
end;

function ShouldRegisterUninstall: Boolean;
begin
  Result := not ValidationMode;
end;

function ShouldCreateIcons: Boolean;
begin
  Result := not ValidationMode;
end;

function NativeUninstallerName(Param: String): String;
begin
  Result := ExtractFileName(ExpandConstant('{uninstallexe}'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Key, Command: String;
begin
  if (CurStep = ssPostInstall) and ShouldRegisterUninstall then
  begin
    Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9338503C-6532-41BE-B6B4-A417CF7DC204}_is1';
    Command := '"' + ExpandConstant('{app}\uninsQuickCapture.exe') + '"';
    if not RegWriteStringValue(HKCU64, Key, 'UninstallString', Command) or
       not RegWriteStringValue(HKCU64, Key, 'QuietUninstallString', Command + ' /SILENT') then
      RaiseException('无法更新轻截的系统卸载入口。请重新运行安装程序修复。');
  end;
end;

function VCRuntimeInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorCode: Integer;
begin
  Result := '';
  if (DirExists(ExpandConstant('{app}\Data')) or DirExists(ExpandConstant('{app}\Captures'))) and
     not FileExists(ExpandConstant('{app}\installed-mode')) then
  begin
    Result := '所选目录包含便携版数据。请使用独立安装目录，保留原目录，并按安装说明迁移需要的数据。';
    Exit;
  end;
  if not VCRuntimeInstalled then
  begin
    if not WizardSilent then
      if SuppressibleMsgBox('轻截需要 Microsoft Visual C++ x64 运行库。是否打开微软官方下载页面？安装运行库后，请重新运行本安装程序。', mbConfirmation, MB_YESNO, IDNO) = IDYES then
        ShellExec('open', 'https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := '缺少 Microsoft Visual C++ x64 运行库，请从微软官方页面安装后重试。';
  end;
end;

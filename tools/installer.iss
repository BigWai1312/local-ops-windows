; Standard Inno Setup wizard. The Python helper only handles storage and ownership checks.
#ifndef ProductId
#define ProductId "LocalOpsWindows"
#endif
#ifndef ProductTitle
#define ProductTitle "Local Ops Windows"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductTitle}
AppVersion={#ProductVersion}
AppPublisher=BigWai1312
AppPublisherURL=https://github.com/BigWai1312/local-ops-windows
DefaultDirName={localappdata}\Programs\LocalOpsWindows
DefaultGroupName={#ProductTitle}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
LicenseFile={#ProjectRoot}\LICENSE
SetupIconFile={#ProjectRoot}\csharp\LocalOpsWindows.Client\Assets\local-ops.ico
UninstallDisplayIcon={app}\versions\{#VersionDirectory}\LocalOpsWindows.Client.exe
OutputDir={#OutputDirectory}
OutputBaseFilename=LocalOpsWindows-Setup
Compression=lzma2/ultra64
LZMADictionarySize=65536
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupMutex={#ProductId}.Setup
Uninstallable=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,{#ProjectRoot}\tools\installer.zh-cn.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked
Name: "autostart"; Description: "登录 Windows 后在通知区域启动"; GroupDescription: "启动选项："; Flags: unchecked

[Files]
Source: "{#MaintenanceExe}"; Flags: dontcopy
Source: "{#MaintenanceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\*"; DestDir: "{app}\versions\{#VersionDirectory}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifdef TestIdentity
Name: "{app}\test-shortcuts\Start"; Filename: "{app}\versions\{#VersionDirectory}\LocalOpsWindows.Client.exe"
Name: "{app}\test-shortcuts\Desktop"; Filename: "{app}\versions\{#VersionDirectory}\LocalOpsWindows.Client.exe"; Tasks: desktopicon
#else
Name: "{autoprograms}\{#ProductTitle}"; Filename: "{app}\versions\{#VersionDirectory}\LocalOpsWindows.Client.exe"
Name: "{autodesktop}\{#ProductTitle}"; Filename: "{app}\versions\{#VersionDirectory}\LocalOpsWindows.Client.exe"; Tasks: desktopicon
#endif

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#ProductId}"; ValueData: """{app}\versions\{#VersionDirectory}\LocalOpsWindows.Client.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#ProductId}"; Flags: deletevalue; Check: AutostartDisabled

[Run]
Filename: "{app}\versions\{#VersionDirectory}\LocalOpsWindows.Client.exe"; Description: "启动 Local Ops Windows"; Flags: postinstall nowait skipifsilent; Check: InstallationFinalized
Filename: "{app}\versions\{#VersionDirectory}\LocalOpsWindows.Client.exe"; Flags: postinstall nowait skipifnotsilent; Check: LaunchAfterUpdate

[UninstallDelete]
Type: files; Name: "{app}\current.json"

[Code]
var
  Finalized: Boolean;

function InstallationFinalized: Boolean;
begin
  Result := Finalized;
end;

function AutostartDisabled: Boolean;
begin
  Result := not WizardIsTaskSelected('autostart');
end;

function LaunchAfterUpdate: Boolean;
begin
  Result := Finalized and WizardSilent and (ExpandConstant('{param:LAUNCHAFTERUPDATE|0}') = '1');
end;

function RunMaintenance(Arguments: String; Installed: Boolean): String;
var
  Helper, ErrorPath: String;
  ErrorText: AnsiString;
  ExitCode: Integer;
begin
  Result := '';
  ErrorPath := ExpandConstant('{tmp}\local-ops-install-error.txt');
  DeleteFile(ErrorPath);
  if Installed then
    Helper := ExpandConstant('{app}\LocalOpsWindows.Maintenance.exe')
  else begin
    ExtractTemporaryFile('LocalOpsWindows.Maintenance.exe');
    Helper := ExpandConstant('{tmp}\LocalOpsWindows.Maintenance.exe');
  end;
  Arguments := Arguments + ' --silent --install-dir "' + ExpandConstant('{app}') +
    '" --error-file "' + ErrorPath + '"';
  if not Exec(Helper, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := '无法启动安装维护程序。'
  else if ExitCode <> 0 then begin
    Result := '安装检查失败，请退出本程序后重试。';
    if LoadStringFromFile(ErrorPath, ErrorText) then
      Result := UTF8Decode(ErrorText);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Arguments, UpdatePid: String;
begin
  Arguments := '--native-prepare';
  UpdatePid := ExpandConstant('{param:UPDATEPID|0}');
  if StrToIntDef(UpdatePid, 0) > 0 then
    Arguments := Arguments + ' --update-pid ' + UpdatePid;
  Result := RunMaintenance(Arguments, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Failure: String;
begin
  if CurStep = ssPostInstall then begin
    Failure := RunMaintenance('--native-finalize {#VersionDirectory}', True);
    if Failure <> '' then
      RaiseException(Failure);
    Finalized := True;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if Finalized then Result := 0 else Result := 1;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Failure: String;
begin
  if CurUninstallStep = usUninstall then begin
    Failure := RunMaintenance('--native-uninstall', True);
    if Failure <> '' then
      RaiseException(Failure);
  end;
end;

#ifndef AppVersion
  #define AppVersion "0.1.0-dev"
#endif
#ifndef PackageRoot
  #define PackageRoot "."
#endif
#ifndef InstallRoot
  #define InstallRoot "{localappdata}\\Programs\\LoomLCI"
#endif
#ifndef LoomRoot
  #define LoomRoot "{localappdata}\\LoomLCI"
#endif
#ifndef LoomAppId
  #define LoomAppId "LoomLCI.38a7c958-cc59-44ed-9f58-29310a508c16"
#endif

[Setup]
AppId={#LoomAppId}
AppName=LoomLCI
AppVersion={#AppVersion}
AppPublisher=LoomLCI
DefaultDirName={#InstallRoot}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=.
OutputBaseFilename=LoomLCI-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes
UninstallDisplayIcon={app}\LoomLCI.Launcher.exe
UninstallDisplayName=LoomLCI
CreateUninstallRegKey=yes
UsePreviousAppDir=no
DirExistsWarning=no
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#PackageRoot}\*"; DestDir: "{tmp}\LoomLCI-Package"; Flags: recursesubdirs createallsubdirs deleteafterinstall ignoreversion

[UninstallDelete]
Type: filesandordirs; Name: "{app}\versions"
Type: filesandordirs; Name: "{app}\tools"
Type: files; Name: "{app}\LoomLCI.Launcher.exe"
Type: filesandordirs; Name: "{#LoomRoot}"

[Code]
var
  TunnelIdPage: TInputQueryWizardPage;
  RuntimeKeyPage: TInputQueryWizardPage;
  ConfigurationExitCode: Integer;

function ParamValue(const Name: String): String;
begin
  Result := Trim(ExpandConstant('{param:' + Name + '|}'));
end;

function EffectiveTunnelId(): String;
begin
  Result := ParamValue('TUNNELID');
  if Result = '' then
    Result := Trim(TunnelIdPage.Values[0]);
end;

function EffectiveRuntimeKeyFile(): String;
begin
  Result := ParamValue('RUNTIMEKEYFILE');
end;

procedure InitializeWizard();
begin
  TunnelIdPage := CreateInputQueryPage(
    wpWelcome,
    'Conectar LoomLCI',
    'Tunnel ID',
    'Ingresá el Tunnel ID que usará esta PC.');
  TunnelIdPage.Add('Tunnel ID:', False);

  RuntimeKeyPage := CreateInputQueryPage(
    TunnelIdPage.ID,
    'Credencial de LoomLCI',
    'Runtime API key',
    'Ingresá la Runtime API key de esta PC. La clave no se pasa por command line.');
  RuntimeKeyPage.Add('Runtime API key:', True);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = TunnelIdPage.ID) and (ParamValue('TUNNELID') <> '') then
    Result := True
  else if (PageID = RuntimeKeyPage.ID) and (ParamValue('RUNTIMEKEYFILE') <> '') then
    Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Value: String;
begin
  Result := True;

  if CurPageID = TunnelIdPage.ID then
  begin
    Value := Trim(TunnelIdPage.Values[0]);
    if (Length(Value) <= Length('tunnel_')) or (Pos('tunnel_', Value) <> 1) then
    begin
      MsgBox('El Tunnel ID debe comenzar con "tunnel_".', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = RuntimeKeyPage.ID then
  begin
    if Trim(RuntimeKeyPage.Values[0]) = '' then
    begin
      MsgBox('La Runtime API key no puede estar vacía.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure ConfigureLoomLCI();
var
  LauncherPath: String;
  PackagePath: String;
  InstallPath: String;
  DataPath: String;
  TunnelId: String;
  RuntimeKeyFile: String;
  TempRuntimeKeyFile: String;
  Params: String;
  ResultCode: Integer;
  CreatedTempKey: Boolean;
begin
  PackagePath := ExpandConstant('{tmp}\LoomLCI-Package');
  LauncherPath := PackagePath + '\LoomLCI.Launcher.exe';
  InstallPath := ExpandConstant('{app}');
  DataPath := ExpandConstant('{#LoomRoot}\deployment');
  TunnelId := EffectiveTunnelId();
  RuntimeKeyFile := EffectiveRuntimeKeyFile();
  CreatedTempKey := False;

  if (Length(TunnelId) <= Length('tunnel_')) or (Pos('tunnel_', TunnelId) <> 1) then
    RaiseException('Tunnel ID inválido.');

  if RuntimeKeyFile = '' then
  begin
    if Trim(RuntimeKeyPage.Values[0]) = '' then
      RaiseException('La Runtime API key no puede estar vacía.');

    TempRuntimeKeyFile := ExpandConstant('{tmp}\loomlci-runtime-api-key.txt');
    if not SaveStringToFile(
      TempRuntimeKeyFile,
      Trim(RuntimeKeyPage.Values[0]),
      False) then
      RaiseException('No se pudo preparar temporalmente la Runtime API key.');

    RuntimeKeyFile := TempRuntimeKeyFile;
    CreatedTempKey := True;
  end
  else if not FileExists(RuntimeKeyFile) then
    RaiseException('No existe el archivo indicado por /RUNTIMEKEYFILE.');

  Params :=
    'setup' +
    ' --package-root ' + AddQuotes(PackagePath) +
    ' --install-root ' + AddQuotes(InstallPath) +
    ' --data-root ' + AddQuotes(DataPath) +
    ' --tunnel-id ' + AddQuotes(TunnelId) +
    ' --runtime-key-file ' + AddQuotes(RuntimeKeyFile) +
    ' --alias loomlci-installed';

  if ParamValue('NOSHORTCUTS') = '1' then
    Params := Params + ' --no-shortcut';

  try
    if not Exec(
      LauncherPath,
      Params,
      PackagePath,
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
      RaiseException('No se pudo ejecutar LoomLCI.Launcher.');

    if ResultCode <> 0 then
      RaiseException(
        Format('La configuración de LoomLCI falló (exit code %d).', [ResultCode]));
  finally
    if CreatedTempKey then
      DeleteFile(TempRuntimeKeyFile);
  end;
end;

procedure StopInstalledRuntime();
var
  LauncherPath: String;
  BatchPath: String;
  BatchText: String;
  ResultCode: Integer;
begin
  LauncherPath := ExpandConstant('{app}\LoomLCI.Launcher.exe');
  if not FileExists(LauncherPath) then
    exit;

  BatchPath := ExpandConstant('{tmp}\loomlci-uninstall-stop.cmd');
  BatchText :=
    '@echo off' + #13#10 +
    'set "LOOMLCI_INSTALL_ROOT=' + ExpandConstant('{app}') + '"' + #13#10 +
    'set "LOOMLCI_DATA_ROOT=' +
      ExpandConstant('{#LoomRoot}\deployment') + '"' + #13#10 +
    '"' + LauncherPath + '" stop' + #13#10 +
    'exit /b %errorlevel%' + #13#10;

  if not SaveStringToFile(BatchPath, BatchText, False) then
    exit;

  try
    Exec(
      ExpandConstant('{cmd}'),
      '/d /s /c ' + AddQuotes(BatchPath),
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);
  finally
    DeleteFile(BatchPath);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { Preserve a nonzero exit code even if /SUPPRESSMSGBOXES
      catches and acknowledges a post-install exception. }
    ConfigurationExitCode := 100;
    ConfigureLoomLCI();
    ConfigurationExitCode := 0;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := ConfigurationExitCode;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopInstalledRuntime();
end;

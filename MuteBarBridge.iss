#define AppName "MuteBar Bridge"
#define AppVersion "1.0.0"
#define AppPublisher "MuteBar Bridge"
#define AppExeName "MuteBarBridge.exe"
#define BuildOutput "..\bin\Debug\net8.0-windows\win-x64"

[Setup]
AppId={{62DAF532-763D-49D2-9204-9D7C72CBB2A0}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\MuteBar Bridge
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=MuteBarBridge-Setup-{#AppVersion}
SetupIconFile=..\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "{#BuildOutput}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\regsvr32.exe"; Parameters: "/s /u ""{app}\UnityCaptureFilter64.dll"""; Flags: runhidden waituntilterminated

[Code]

const
  UnityCaptureFilterUrl =
    'https://raw.githubusercontent.com/schellingb/UnityCapture/master/Install/UnityCaptureFilter64.dll';

function DownloadUnityCaptureFilter(const Destination: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -Uri ''https://raw.githubusercontent.com/schellingb/UnityCapture/master/Install/UnityCaptureFilter64.dll'' -OutFile ''' + Destination + '''"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  Result := Result and FileExists(Destination);
end;

function RegisterUnityCaptureFilter(const FilterPath: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(
    ExpandConstant('{sys}\regsvr32.exe'),
    '/s "' + FilterPath + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  TmpFile: String;
  FilterPath: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  TmpFile := ExpandConstant('{tmp}\UnityCaptureFilter64.dll');
  FilterPath := ExpandConstant('{app}\UnityCaptureFilter64.dll');

  if not DownloadUnityCaptureFilter(TmpFile) then
    Abort;

  if not FileCopy(TmpFile, FilterPath, False) then
  begin
    MsgBox('Failed to copy UnityCapture DLL into the installation folder.',
      mbError, MB_OK);
    Abort;
  end;

  if not RegisterUnityCaptureFilter(FilterPath) then
  begin
    MsgBox(
      'UnityCapture downloaded successfully but registration failed.',
      mbError,
      MB_OK);
    Abort;
  end;
end;

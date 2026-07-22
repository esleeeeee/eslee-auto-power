#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif
#ifndef MyAppLanguage
  #define MyAppLanguage "ko"
#endif

#if MyAppLanguage == "en"
  #define MyRemoveLabel "Uninstall"
  #define MyRunDescription "Run eslee Auto Power"
  #define MyCleanupError "Failed to clean up eslee Auto Power state. Uninstall has been stopped. Error code: %d"
#else
  #define MyRemoveLabel "제거"
  #define MyRunDescription "eslee Auto Power 실행"
  #define MyCleanupError "eslee Auto Power 상태 정리에 실패했습니다. 제거를 중단합니다. 오류 코드: %d"
#endif

[Setup]
AppId={{5E13EC8A-0A03-4EA1-9E0B-D72F319D1A3A}
AppName=eslee Auto Power
AppVersion={#MyAppVersion}
AppPublisher=eslee
DefaultDirName={autopf}\eslee Auto Power
DefaultGroupName=eslee Auto Power
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=eslee-auto-power-v{#MyAppVersion}-{#MyAppLanguage}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayName=eslee Auto Power
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=eslee
VersionInfoDescription=eslee Auto Power Setup ({#MyAppLanguage})
VersionInfoProductName=eslee Auto Power
SetupIconFile=..\src\AutoPower.App\Assets\eslee-auto-power.ico
UninstallDisplayIcon={app}\AutoPower.App.exe

[Languages]
#if MyAppLanguage == "en"
Name: "english"; MessagesFile: "compiler:Default.isl"
#else
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif

[Dirs]
Name: "{commonappdata}\eslee\AutoPower"; Permissions: users-modify

[Files]
Source: "..\artifacts\publish\{#MyAppLanguage}\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\eslee Auto Power"; Filename: "{app}\AutoPower.App.exe"
Name: "{group}\eslee Auto Power {#MyRemoveLabel}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\AutoPower.App.exe"; Description: "{#MyRunDescription}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\eslee\AutoPower"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    ResultCode := -1;
    if (not Exec(ExpandConstant('{app}\AutoPower.Helper.exe'), 'cleanup-app', '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    begin
      RaiseException(Format('{#MyCleanupError}', [ResultCode]));
    end;
  end;
end;

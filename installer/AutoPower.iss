#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif
#ifndef MyPublishDir
  #define MyPublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
; Both legacy ko/en installers used this AppId. Keep it for in-place upgrades.
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
OutputBaseFilename=eslee-auto-power-v{#MyAppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
LanguageDetectionMethod=uilanguage
UsePreviousLanguage=no
UninstallDisplayName=eslee Auto Power
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=eslee
VersionInfoDescription=eslee Auto Power Setup
VersionInfoProductName=eslee Auto Power
SetupIconFile=..\src\AutoPower.App\Assets\eslee-auto-power.ico
UninstallDisplayIcon={app}\AutoPower.App.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(CompilerPath + "\Languages\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#elif FileExists(CompilerPath + "\Languages\Unofficial\Korean.isl")
Name: "korean"; MessagesFile: "compiler:Languages\Unofficial\Korean.isl"
#else
#error Korean.isl is required. Install Inno Setup 6.5 or newer with its language files.
#endif

[CustomMessages]
english.RemoveLabel=Uninstall
korean.RemoveLabel=제거
english.RunDescription=Run eslee Auto Power
korean.RunDescription=eslee Auto Power 실행
english.CleanupError=Failed to clean up eslee Auto Power state. Uninstall has been stopped. Error code: %d
korean.CleanupError=eslee Auto Power 상태 정리에 실패했습니다. 제거를 중단합니다. 오류 코드: %d

[Dirs]
Name: "{commonappdata}\eslee\AutoPower"; Permissions: users-modify

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\eslee Auto Power"; Filename: "{app}\AutoPower.App.exe"
Name: "{group}\eslee Auto Power {cm:RemoveLabel}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\AutoPower.App.exe"; Description: "{cm:RunDescription}"; Flags: nowait postinstall skipifsilent

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
      RaiseException(Format(CustomMessage('CleanupError'), [ResultCode]));
    end;
  end;
end;

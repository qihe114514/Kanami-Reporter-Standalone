#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

[Setup]
AppId={{8E6DC812-7A25-4E81-9BC6-1632A71CE54B}
AppName=Kanami Reporter
AppVersion={#MyAppVersion}
AppPublisher=Yiyan
AppPublisherURL=https://github.com/qihe114514/Kanami-Reporter-Standalone
AppSupportURL=https://github.com/qihe114514/Kanami-Reporter-Standalone
AppUpdatesURL=https://github.com/qihe114514/Kanami-Reporter-Standalone/releases
DefaultDirName={localappdata}\Programs\KanamiReporter
DefaultGroupName=Kanami Reporter
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=KanamiReporter-Setup-x64-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\KanamiReporter.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Assets\AppIcon.ico
CloseApplications=yes
CloseApplicationsFilter=KanamiReporter.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Kanami Reporter"; Filename: "{app}\KanamiReporter.exe"
Name: "{group}\卸载 Kanami Reporter"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Kanami Reporter"; Filename: "{app}\KanamiReporter.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\KanamiReporter.exe"; Description: "启动 Kanami Reporter"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

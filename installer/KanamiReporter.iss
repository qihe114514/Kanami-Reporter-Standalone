#ifndef MyAppVersion
  #define MyAppVersion "1.0.4"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

[Setup]
AppId={{8E6DC812-7A25-4E81-9BC6-1632A71CE54B}
AppName=香奈美x黑潮爆破
AppVersion={#MyAppVersion}
AppVerName=香奈美x黑潮爆破 {#MyAppVersion}
AppPublisher=Yiyan
AppPublisherURL=https://github.com/qihe114514/Kanami-Reporter-Standalone
AppSupportURL=https://github.com/qihe114514/Kanami-Reporter-Standalone
AppUpdatesURL=https://github.com/qihe114514/Kanami-Reporter-Standalone/releases
DefaultDirName={localappdata}\Programs\KanamiReporter
DefaultGroupName=香奈美x黑潮爆破
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
UninstallDisplayName=香奈美x黑潮爆破
CloseApplications=yes
CloseApplicationsFilter=KanamiReporter.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\香奈美x黑潮爆破"; Filename: "{app}\KanamiReporter.exe"
Name: "{group}\卸载 香奈美x黑潮爆破"; Filename: "{uninstallexe}"
Name: "{autodesktop}\香奈美x黑潮爆破"; Filename: "{app}\KanamiReporter.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\KanamiReporter.exe"; Description: "启动 香奈美x黑潮爆破"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; ──────────────────────────────────────────────────────────────
; AgentBrook Helper — Inno Setup 安装脚本
; 参数 (由 ISCC 命令行 /D 传入):
;   AppVersion  — 版本号，例如 1.0.11
;   SourceDir   — dotnet publish 输出目录
;   OutputDir   — 安装包输出目录
;   Arch        — 架构标识 (x64 / arm64)
; ──────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{B5E8F3A2-7C4D-4E91-A6B0-3F8D2C1E9A7B}
AppName=AgentBrook Helper
AppVersion={#AppVersion}
AppVerName=AgentBrook Helper {#AppVersion}
AppPublisher=AgentBrook
AppPublisherURL=https://github.com/ChenGan666/AgentBrook-Helper
AppSupportURL=https://github.com/ChenGan666/AgentBrook-Helper/issues
DefaultDirName={autopf}\AgentBrook Helper
DefaultGroupName=AgentBrook Helper
UninstallDisplayIcon={app}\AgentBrook.Helper.exe
UninstallDisplayName=AgentBrook Helper
OutputBaseFilename=AgentBrook.Helper-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; 安装界面
WizardStyle=modern
WizardSizePercent=120
SetupIconFile=..\Assets\app-icon.ico
; 权限：按用户安装（不需要管理员权限）
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 架构
#if Arch == "arm64"
ArchitecturesInstallIn64BitMode=arm64
ArchitecturesAllowed=arm64
#else
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
#endif
; 版本信息
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=AgentBrook
VersionInfoDescription=AgentBrook Helper 安装程序
VersionInfoProductName=AgentBrook Helper
VersionInfoProductVersion={#AppVersion}
; 输出目录（由命令行 /O 指定，此处做后备）
#ifdef OutputDir
OutputDir={#OutputDir}
#else
OutputDir=..\..\publish
#endif
; 卸载时清理应用目录（用户数据在 workspace，不受影响）
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "其他选项:"; Flags: unchecked

[Files]
; 复制 dotnet publish 输出的所有文件
#ifdef SourceDir
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Icons]
; 开始菜单
Name: "{group}\AgentBrook Helper"; Filename: "{app}\AgentBrook.Helper.exe"; Comment: "跨平台桌面 AI 智能体"
Name: "{group}\卸载 AgentBrook Helper"; Filename: "{uninstallexe}"
; 桌面快捷方式（可选）
Name: "{autodesktop}\AgentBrook Helper"; Filename: "{app}\AgentBrook.Helper.exe"; Tasks: desktopicon; Comment: "跨平台桌面 AI 智能体"

[Registry]
; 开机自启动（可选）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AgentBrook Helper"; ValueData: """{app}\AgentBrook.Helper.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; 安装完成后启动程序（可选）
Filename: "{app}\AgentBrook.Helper.exe"; Description: "启动 AgentBrook Helper"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; 卸载时清理可能残留的日志文件（用户数据在 workspace 目录，不会被删除）
Type: filesandordirs; Name: "{app}\logs"

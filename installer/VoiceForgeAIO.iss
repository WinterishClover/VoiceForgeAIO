; VoiceForge AIO -- Inno Setup installer script
;
; Requires Inno Setup 6+ (free): https://jrsoftware.org/isinfo.php
;
; ---- Before compiling this script ----
; 1. Publish the self-contained build first (produces the folder this script
;    packages):
;      dotnet publish -c Release -p:PublishProfile=FolderProfile
;    from the VoiceForgeAIO project folder. Confirm the output folder path
;    below (SourceDir) matches where that actually landed.
;
; 2. Download the WebView2 "Evergreen Bootstrapper" from Microsoft and place
;    it at installer\redist\MicrosoftEdgeWebView2Setup.exe (this repo doesn't
;    include it -- it's a small ~2MB file from Microsoft, not VoiceForge AIO's
;    own code, so it isn't bundled here):
;      https://developer.microsoft.com/microsoft-edge/webview2/
;    (Look for "Evergreen Bootstrapper" under the download options.)
;
; 3. Open this .iss file in Inno Setup and click Compile (or run ISCC.exe from
;    the command line). Output lands in installer_output\VoiceForgeAIO-Setup.exe.

#define MyAppName "VoiceForge AIO"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "WinterishClover"
#define MyAppURL "https://www.patreon.com/cw/winterishclover"
#define MyAppExeName "VoiceForgeAIO.exe"
#define SourceDir "..\VoiceForgeAIO\bin\Release\net8.0-windows\publish"

[Setup]
AppId={{B4E1B6A0-7F0B-4C7E-9A9C-2A3E6F1D8C40}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename=VoiceForgeAIO-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Everything from the self-contained publish output, minus debug symbols.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

; WebView2 bootstrapper -- see step 2 in the header comment above. This is
; only a few MB and just checks/installs the runtime; it doesn't download the
; full WebView2 payload unless the runtime is actually missing.
Source: "redist\MicrosoftEdgeWebView2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Only run the bootstrapper if WebView2 genuinely isn't already installed --
; previously this always ran, which meant every install did a slow network
; round-trip to Microsoft even when nothing needed installing. Most testers
; already have WebView2 (it ships with Windows 10/11 in most cases), so this
; check makes the common case fast.
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2 Runtime (this may take a minute)..."; Flags: waituntilterminated; Check: not WebView2IsInstalled
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Detection method per Microsoft's own docs:
// https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution#detect-if-a-suitable-webview2-runtime-is-already-installed
// WebView2 registers under the 32-bit registry view (WOW6432Node) regardless
// of OS bitness -- HKLM32 handles that redirection automatically. Per-user
// installs (increasingly common) register under HKCU instead, so that's
// checked too. Presence of a non-empty "pv" value under the Clients GUID key
// means it's installed; this is the officially recommended way to check.
const
  WV2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2IsInstalled: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, WV2ClientKey, 'pv', Version) and (Version <> '')) or
    (RegQueryStringValue(HKLM64, WV2ClientKey, 'pv', Version) and (Version <> '')) or
    (RegQueryStringValue(HKCU, WV2ClientKey, 'pv', Version) and (Version <> ''));
end;

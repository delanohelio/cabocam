; CaboCam installer (Inno Setup 6). Built by .github/workflows/windows.yml:
;   iscc /DAppVersion=1.2.3 /DSourceDir=<dist\CaboCam> /DOutputDir=<dist> windows\installer\CaboCam.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\CaboCam"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{6F3C1E2A-8B7D-4C59-9A1E-CAB0CA3E0001}
AppName=CaboCam
AppVersion={#AppVersion}
AppVerName=CaboCam {#AppVersion}
AppPublisher=CaboCam
AppPublisherURL=https://github.com/delanohelio/cabocam
AppSupportURL=https://github.com/delanohelio/cabocam/issues
DefaultDirName={autopf}\CaboCam
DefaultGroupName=CaboCam
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename=CaboCam-Setup-{#AppVersion}
SetupIconFile=..\desktop\Assets\CaboCam.ico
UninstallDisplayIcon={app}\CaboCam.exe
UninstallDisplayName=CaboCam
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar ícone na Área de Trabalho"; GroupDescription: "Atalhos:"
Name: "autostart"; Description: "Iniciar o CaboCam com o Windows"; GroupDescription: "Opções:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "install.ps1,uninstall.ps1,LEIA-ME.txt"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CaboCam"; Filename: "{app}\CaboCam.exe"; WorkingDir: "{app}"; Comment: "iPhone como webcam"
Name: "{autodesktop}\CaboCam"; Filename: "{app}\CaboCam.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CaboCam"; \
    ValueData: """{app}\CaboCam.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; The Frame Server runs as LocalService: grant read/execute to Users and LOCAL SERVICE.
Filename: "{sys}\icacls.exe"; Parameters: """{app}"" /grant *S-1-5-32-545:(OI)(CI)RX *S-1-5-19:(OI)(CI)RX /T /C /Q"; \
    Flags: runhidden waituntilterminated; StatusMsg: "Ajustando permissões..."
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\CaboCam.VirtualCamera.dll"""; \
    Flags: runhidden waituntilterminated; StatusMsg: "Registrando a câmera virtual..."
Filename: "{app}\CaboCam.exe"; Description: "Abrir o CaboCam"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM CaboCam.exe /F"; Flags: runhidden; RunOnceId: "KillCaboCam"
Filename: "{sys}\regsvr32.exe"; Parameters: "/s /u ""{app}\CaboCam.VirtualCamera.dll"""; Flags: runhidden waituntilterminated; RunOnceId: "UnregVcam"
Filename: "{sys}\net.exe"; Parameters: "stop FrameServer /y"; Flags: runhidden waituntilterminated; RunOnceId: "StopFrameServer"

[Code]
function RunHidden(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

// Upgrades: stop CaboCam and release the old DLL held by the Windows Camera Frame Server.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldDll: String;
begin
  Result := '';
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM CaboCam.exe /F');
  OldDll := ExpandConstant('{app}\CaboCam.VirtualCamera.dll');
  if FileExists(OldDll) then
  begin
    RunHidden(ExpandConstant('{sys}\regsvr32.exe'), '/s /u "' + OldDll + '"');
    RunHidden(ExpandConstant('{sys}\net.exe'), 'stop FrameServer /y');
  end;
end;

function AppleServiceInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\Apple Mobile Device Service');
end;

procedure CurrentStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not AppleServiceInstalled()) then
    MsgBox('O "Apple Mobile Device Service" não foi encontrado.' + #13#10 + #13#10 +
           'Instale o app "Dispositivos Apple" (Apple Devices) pela Microsoft Store, ' +
           'ou o iTunes, para o CaboCam conversar com o iPhone pelo cabo USB.',
           mbInformation, MB_OK);
end;

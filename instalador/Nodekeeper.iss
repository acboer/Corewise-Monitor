; Instalador do Nodekeeper (Inno Setup 6). Gere com instalador\gerar.ps1, que publica os executáveis antes.
; Três tipos: Completo (painel + serviço), Só o serviço (NAS, servidor, render farm) e Só o painel.
; Silencioso (render farm): NodekeeperSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /TYPE=servico

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define Src "..\publish\nodekeeper"

[Setup]
AppId={{8C1F4E2A-5B7D-4E9C-9A31-6C2E0F7D4B15}
AppName=Nodekeeper
AppVersion={#AppVersion}
AppVerName=Nodekeeper {#AppVersion}
AppPublisher=Nodekeeper
AppPublisherURL=https://github.com/acboer/Nodekeeper
AppSupportURL=https://github.com/acboer/Nodekeeper/issues
AppUpdatesURL=https://github.com/acboer/Nodekeeper/releases
AppCopyright=Copyright (c) 2026 Nodekeeper contributors. MIT License.
LicenseFile=..\LICENSE
DefaultDirName={autopf}\Nodekeeper
DefaultGroupName=Nodekeeper
; Atualizando o Corewise (mesmo AppId): instala na pasta nova, não na antiga "Corewise".
UsePreviousAppDir=no
UsePreviousGroup=no
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\icons\app.ico
UninstallDisplayIcon={app}\Nodekeeper.exe
UninstallDisplayName=Nodekeeper
OutputDir=..\publish
OutputBaseFilename=NodekeeperSetup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Nodekeeper
VersionInfoProductTextVersion={#AppVersion}
VersionInfoCopyright=Copyright (c) 2026 Nodekeeper contributors. MIT License.
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "aviso-en.txt"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"; InfoBeforeFile: "aviso-pt.txt"; LicenseFile: "licenca-pt.txt"

[CustomMessages]
ptbr.TypeFull=Completo: painel, widget e serviço (este PC aparece no painel)
en.TypeFull=Full: dashboard, widget and service (this PC shows up in the dashboard)
ptbr.TypeService=Só o serviço: este PC é visto por outro (NAS, servidor, render farm)
en.TypeService=Service only: this PC is watched from another one (NAS, server, render farm)
ptbr.TypePanel=Só o painel: acompanhar outras máquinas
en.TypePanel=Dashboard only: watch other machines
ptbr.TypeCustom=Personalizado
en.TypeCustom=Custom
ptbr.CompPanel=Painel e widget (Nodekeeper.exe)
en.CompPanel=Dashboard and widget (Nodekeeper.exe)
ptbr.CompService=Serviço (Nodekeeper.Service.exe): lê os sensores, roda com o Windows
en.CompService=Service (Nodekeeper.Service.exe): reads the sensors, runs with Windows
ptbr.Pairing=Código de pareamento
en.Pairing=Pairing code
ptbr.InstallingService=Instalando o serviço Nodekeeper...
en.InstallingService=Installing the Nodekeeper service...
ptbr.OpenNow=Abrir o Nodekeeper
en.OpenNow=Open Nodekeeper
ptbr.GroupDriver=Sensores:
en.GroupDriver=Sensors:
ptbr.TaskPawnIO=Instalar o driver PawnIO (temperatura e consumo da CPU, ventoinhas e temperatura da RAM; baixado de github.com/namazso/PawnIO.Setup)
en.TaskPawnIO=Install the PawnIO driver (CPU temperature and power, fans and RAM temperature; downloaded from github.com/namazso/PawnIO.Setup)
ptbr.DownloadingPawnIO=Baixando o driver PawnIO...
en.DownloadingPawnIO=Downloading the PawnIO driver...
ptbr.InstallingPawnIO=Instalando o driver PawnIO...
en.InstallingPawnIO=Installing the PawnIO driver...
ptbr.PawnIOFailed=Não foi possível instalar o driver PawnIO (%1). O Nodekeeper funciona sem ele, mas sem a temperatura e o consumo da CPU, as ventoinhas e a temperatura da RAM. Instale depois por pawnio.eu e reinicie o serviço Nodekeeper.
en.PawnIOFailed=Could not install the PawnIO driver (%1). Nodekeeper works without it, but without CPU temperature and power, fans and RAM temperature. Install it later from pawnio.eu and restart the Nodekeeper service.

[Types]
Name: "completo"; Description: "{cm:TypeFull}"
Name: "servico"; Description: "{cm:TypeService}"
Name: "painel"; Description: "{cm:TypePanel}"
Name: "custom"; Description: "{cm:TypeCustom}"; Flags: iscustom

[Components]
Name: "painel"; Description: "{cm:CompPanel}"; Types: completo painel
Name: "servico"; Description: "{cm:CompService}"; Types: completo servico

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Components: painel; Flags: unchecked
; Só aparece quando o PawnIO falta ou é anterior à 2.0 (o LibreHardwareMonitor 0.9 precisa dele). Marcado por padrão,
; também na instalação silenciosa; /TASKS="!pawnio" pula.
Name: "pawnio"; Description: "{cm:TaskPawnIO}"; GroupDescription: "{cm:GroupDriver}"; Components: servico; Check: PawnIONeeded

[Files]
Source: "{#Src}\Nodekeeper.exe"; DestDir: "{app}"; Components: painel; Flags: ignoreversion
Source: "{#Src}\Nodekeeper.Service.exe"; DestDir: "{app}"; Components: servico; Flags: ignoreversion

[InstallDelete]
; Restos do nome anterior (Corewise, 0.12–0.13): pasta do programa, atalhos do menu Iniciar e da área de trabalho.
; Os dados (%ProgramData%\Corewise e %AppData%\Corewise) não são apagados aqui: o serviço e o painel os movem para
; ...\Nodekeeper na primeira execução.
Type: filesandordirs; Name: "{autopf}\Corewise"
Type: filesandordirs; Name: "{autoprograms}\Corewise"
Type: files; Name: "{autodesktop}\Corewise.lnk"

[Icons]
Name: "{autoprograms}\Nodekeeper\Nodekeeper"; Filename: "{app}\Nodekeeper.exe"; Components: painel
Name: "{autoprograms}\Nodekeeper\{cm:Pairing}"; Filename: "{app}\Nodekeeper.Service.exe"; Parameters: "parear --pausar"; Components: servico
Name: "{autodesktop}\Nodekeeper"; Filename: "{app}\Nodekeeper.exe"; Tasks: desktopicon

[Registry]
; "Iniciar com o Windows" é gravado pelo próprio painel; aqui só some na desinstalação.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Nodekeeper"; Flags: uninsdeletevalue
; Entrada do nome anterior: o painel a troca pela nova na primeira execução (mantendo a escolha); some na desinstalação.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Corewise"; Flags: uninsdeletevalue

[Run]
; Cria (ou recria) o serviço e a regra de firewall e remove os serviços e regras antigos ("Corewise", "HwMonCollector").
Filename: "{app}\Nodekeeper.Service.exe"; Parameters: "instalar"; StatusMsg: "{cm:InstallingService}"; Flags: runhidden waituntilterminated; Components: servico
Filename: "{app}\Nodekeeper.exe"; Description: "{cm:OpenNow}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Components: painel

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im Nodekeeper.exe"; Flags: runhidden; RunOnceId: "ClosePanel"
Filename: "{app}\Nodekeeper.Service.exe"; Parameters: "desinstalar"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"; Components: servico

[Code]
{ Antes de copiar: para o serviço (o atual e os antigos Corewise e HwMonCollector) e fecha o painel (o atual e os
  antigos Corewise.exe e HwMon.exe),
  senão os executáveis estão em uso. Os dados ficam; o serviço e o painel migram as pastas antigas sozinhos. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: Integer;
begin
  Exec(ExpandConstant('{sys}\net.exe'), 'stop Nodekeeper', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\net.exe'), 'stop Corewise', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\net.exe'), 'stop HwMonCollector', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Nodekeeper.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Corewise.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im HwMon.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
  Result := '';
end;

{ Driver PawnIO: o LibreHardwareMonitor (dentro do serviço) lê por ele a temperatura e o consumo da CPU, as ventoinhas
  e a temperatura da RAM. Não vem junto: o instalador oficial é baixado do projeto e rodado em modo silencioso, antes
  de o serviço ser instalado (que já abre o LHM com o driver). Falhar não impede a instalação do Nodekeeper.
  A desinstalação não remove o PawnIO: outros programas (LibreHardwareMonitor, FanControl...) usam o mesmo driver. }
const
  PawnIOKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
  PawnIOUrl = 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe';

function PawnIONeeded: Boolean;
var
  Version: String;
begin
  if RegQueryStringValue(HKLM64, PawnIOKey, 'DisplayVersion', Version) then
    Result := (Version = '') or (StrToIntDef(Copy(Version, 1, Pos('.', Version + '.') - 1), 0) < 2)
  else
    Result := not RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

procedure InstallPawnIO;
var
  Setup: String;
  R: Integer;
begin
  try
    WizardForm.StatusLabel.Caption := CustomMessage('DownloadingPawnIO');
    DownloadTemporaryFile(PawnIOUrl, 'PawnIO_setup.exe', '', nil);
    Setup := ExpandConstant('{tmp}\PawnIO_setup.exe');
    WizardForm.StatusLabel.Caption := CustomMessage('InstallingPawnIO');
    { 0 = instalado; 3010 = instalado, pede reinício (o serviço já usa o driver ao iniciar). }
    if not Exec(Setup, '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, R) then
      SuppressibleMsgBox(FmtMessage(CustomMessage('PawnIOFailed'), [SysErrorMessage(R)]), mbInformation, MB_OK, IDOK)
    else if (R <> 0) and (R <> 3010) then
      SuppressibleMsgBox(FmtMessage(CustomMessage('PawnIOFailed'), [IntToStr(R)]), mbInformation, MB_OK, IDOK);
  except
    SuppressibleMsgBox(FmtMessage(CustomMessage('PawnIOFailed'), [GetExceptionMessage]), mbInformation, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { ssInstall: antes de copiar os arquivos e de [Run] criar o serviço, que abre o LHM já com o driver. }
  if (CurStep = ssInstall) and WizardIsTaskSelected('pawnio') then InstallPawnIO;
end;

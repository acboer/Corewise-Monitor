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
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"; InfoBeforeFile: "aviso-pt.txt"

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

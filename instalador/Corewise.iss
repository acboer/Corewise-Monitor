; Instalador do Corewise (Inno Setup 6). Gere com instalador\gerar.ps1, que publica os executáveis antes.
; Três tipos: Completo (painel + serviço), Só o serviço (NAS, servidor, render farm) e Só o painel.
; Silencioso (render farm): CorewiseSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /TYPE=servico

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define Src "..\publish\corewise"

[Setup]
AppId={{8C1F4E2A-5B7D-4E9C-9A31-6C2E0F7D4B15}
AppName=Corewise
AppVersion={#AppVersion}
AppVerName=Corewise {#AppVersion}
AppPublisher=Corewise
DefaultDirName={autopf}\Corewise
DefaultGroupName=Corewise
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\icons\app.ico
UninstallDisplayIcon={app}\Corewise.exe
UninstallDisplayName=Corewise
OutputDir=..\publish
OutputBaseFilename=CorewiseSetup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Corewise
ShowLanguageDialog=auto

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ptbr.TypeFull=Completo: painel, widget e serviço (este PC aparece no painel)
en.TypeFull=Full: dashboard, widget and service (this PC shows up in the dashboard)
ptbr.TypeService=Só o serviço: este PC é visto por outro (NAS, servidor, render farm)
en.TypeService=Service only: this PC is watched from another one (NAS, server, render farm)
ptbr.TypePanel=Só o painel: acompanhar outras máquinas
en.TypePanel=Dashboard only: watch other machines
ptbr.TypeCustom=Personalizado
en.TypeCustom=Custom
ptbr.CompPanel=Painel e widget (Corewise.exe)
en.CompPanel=Dashboard and widget (Corewise.exe)
ptbr.CompService=Serviço (Corewise.Service.exe): lê os sensores, roda com o Windows
en.CompService=Service (Corewise.Service.exe): reads the sensors, runs with Windows
ptbr.Pairing=Código de pareamento
en.Pairing=Pairing code
ptbr.InstallingService=Instalando o serviço Corewise...
en.InstallingService=Installing the Corewise service...
ptbr.OpenNow=Abrir o Corewise
en.OpenNow=Open Corewise

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
Source: "{#Src}\Corewise.exe"; DestDir: "{app}"; Components: painel; Flags: ignoreversion
Source: "{#Src}\Corewise.Service.exe"; DestDir: "{app}"; Components: servico; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Corewise\Corewise"; Filename: "{app}\Corewise.exe"; Components: painel
Name: "{autoprograms}\Corewise\{cm:Pairing}"; Filename: "{app}\Corewise.Service.exe"; Parameters: "parear --pausar"; Components: servico
Name: "{autodesktop}\Corewise"; Filename: "{app}\Corewise.exe"; Tasks: desktopicon

[Registry]
; "Iniciar com o Windows" é gravado pelo próprio painel; aqui só some na desinstalação.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Corewise"; Flags: uninsdeletevalue

[Run]
; Cria (ou recria) o serviço, a regra de firewall e remove o serviço antigo "HwMonCollector", se houver.
Filename: "{app}\Corewise.Service.exe"; Parameters: "instalar"; StatusMsg: "{cm:InstallingService}"; Flags: runhidden waituntilterminated; Components: servico
Filename: "{app}\Corewise.exe"; Description: "{cm:OpenNow}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Components: painel

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im Corewise.exe"; Flags: runhidden; RunOnceId: "ClosePanel"
Filename: "{app}\Corewise.Service.exe"; Parameters: "desinstalar"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"; Components: servico

[Code]
{ Antes de copiar: para o serviço (novo e o antigo HwMonCollector) e fecha o painel (novo e o antigo HwMon.exe),
  senão os executáveis estão em uso. Os dados ficam; o serviço e o painel migram as pastas antigas sozinhos. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: Integer;
begin
  Exec(ExpandConstant('{sys}\net.exe'), 'stop Corewise', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\net.exe'), 'stop HwMonCollector', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Corewise.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im HwMon.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
  Result := '';
end;

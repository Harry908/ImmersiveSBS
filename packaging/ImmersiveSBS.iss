; The ImmersiveSBS installer (Inno Setup 6). One click: no pages before it installs, then a
; finish page. It installs the app (with the models packaged), the Virtual Display Driver when
; it is not there yet (its publisher trusted first, so Windows does not stop to ask, and its
; settings set to one 1920x1080 monitor), and a Start menu entry. Build it with
; packaging\build-installer.ps1, which passes the folders and the version below.

#ifndef AppVersion
  #error Build with packaging\build-installer.ps1
#endif

; The app's single-instance mutex (App.xaml.cs); "{{" is Inno's literal "{".
#define AppMutexName "Local\ImmersiveSBS.{{5C1E6E52-4B7B-4E0B-9C1F-3F1D2B7A9E61}"

[Setup]
AppId={{5BB29ADF-6227-4BE1-9FF3-F4D821602852}
AppName=ImmersiveSBS
AppVersion={#AppVersion}
AppVerName=ImmersiveSBS {#AppVersion}
AppPublisher=ImmersiveSBS
AppPublisherURL=https://github.com/Harry908/ImmersiveSBS
DefaultDirName={autopf}\ImmersiveSBS
; One click: no welcome, folder, Start menu or ready page.
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ShowLanguageDialog=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex={#AppMutexName}
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
SetupIconFile={#RepoDir}\src\ImmersiveSBS\ImmersiveSBS.ico
UninstallDisplayIcon={app}\ImmersiveSBS.exe
UninstallDisplayName=ImmersiveSBS
OutputDir={#OutputDir}
OutputBaseFilename=ImmersiveSBS-Setup-{#AppVersion}
; The models are most of the size and barely compress; normal LZMA2 keeps the build quick.
Compression=lzma2
SolidCompression=yes

[Messages]
FinishedHeadingLabel=ImmersiveSBS is installed
FinishedLabel=Switch your glasses to 3D mode and the desktop appears in both eyes.%n%nFor the 3D theater, add the browser extension: in the Extensions page that opens, turn on Developer mode, choose Load unpacked and pick the folder that opens beside it.

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#DriverDir}\*"; DestDir: "{app}\driver"; Flags: ignoreversion
; The driver's settings, unless it has some: it reads them from here.
Source: "{#RepoDir}\packaging\vdd\vdd_settings.xml"; DestDir: "{sd}\VirtualDisplayDriver"; Flags: onlyifdoesntexist uninsneveruninstall

[Registry]
; Where the driver finds its settings (it defaults to C:\VirtualDisplayDriver), unless set already.
Root: HKLM; Subkey: "SOFTWARE\MikeTheTech\VirtualDisplayDriver"; ValueType: string; ValueName: "VDDPATH"; ValueData: "{sd}\VirtualDisplayDriver"; Flags: createvalueifdoesntexist uninsdeletekeyifempty

[Icons]
Name: "{autoprograms}\ImmersiveSBS"; Filename: "{app}\ImmersiveSBS.exe"

[Run]
; On the finish page, all ticked. The Run value is the app's own Start with Windows setting.
Filename: "{sys}\reg.exe"; Parameters: "add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ImmersiveSBS /t REG_SZ /d ""\""{app}\ImmersiveSBS.exe\"""" /f"; Description: "Start ImmersiveSBS when Windows starts"; Flags: postinstall runhidden runasoriginaluser skipifsilent
Filename: "{cmd}"; Parameters: "/c start """" msedge edge://extensions & start """" explorer ""{app}\extension"""; Description: "Add the browser extension for the 3D theater (opens Edge's extensions page and the folder to load)"; Flags: postinstall runhidden runasoriginaluser skipifsilent
Filename: "{app}\ImmersiveSBS.exe"; Description: "Start ImmersiveSBS now"; Flags: postinstall nowait runasoriginaluser skipifsilent

[Code]
const
  DriverHardwareId = 'Root\MttVDD';
  DriverDevices = 'SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY';
  SignPathThumbprint = '{#SignPathThumbprint}';
  TrustedPublishers = 'SOFTWARE\Microsoft\SystemCertificates\TrustedPublisher\Certificates';
  StateKey = 'Software\ImmersiveSBS';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  DriverNeedsRestart: Boolean;

{ Whether a Virtual Display Driver device exists: a root-enumerated display device with its hardware ID. }
function DriverPresent(): Boolean;
var
  Keys: TArrayOfString;
  Ids: String;
  I: Integer;
begin
  Result := False;
  if not RegGetSubkeyNames(HKLM, DriverDevices, Keys) then Exit;
  for I := 0 to GetArrayLength(Keys) - 1 do
    if RegQueryMultiStringValue(HKLM, DriverDevices + '\' + Keys[I], 'HardwareID', Ids) then
      if Pos(Lowercase(DriverHardwareId), Lowercase(Ids)) > 0 then
      begin
        Result := True;
        Exit;
      end;
end;

function RunHidden(const FileName, Params: String; var Code: Integer): Boolean;
begin
  Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log(Format('%s %s -> %d', [FileName, Params, Code]));
end;

{ Installs the driver unless it is there; what it did is kept for the uninstaller. }
procedure InstallDriver();
var
  Driver, CertUtil: String;
  Code: Integer;
begin
  if DriverPresent() then
  begin
    Log('The Virtual Display Driver is already installed.');
    Exit;
  end;
  Driver := ExpandConstant('{app}\driver');
  CertUtil := ExpandConstant('{sys}\certutil.exe');
  WizardForm.StatusLabel.Caption := 'Installing the virtual display driver...';
  { Its publisher (SignPath Foundation) trusted first, or Windows stops to ask. (certutil
    -verifystore answers 0 whether the certificate is there or not: the store's key tells.) }
  if not RegKeyExists(HKLM, TrustedPublishers + '\' + SignPathThumbprint) then
    if RunHidden(CertUtil, '-f -addstore TrustedPublisher "' + Driver + '\signpath.cer"', Code) and (Code = 0) then
      RegWriteDWordValue(HKLM, StateKey, 'AddedPublisher', 1);
  { devcon: 0 installed, 1 installed and a restart is needed. }
  if RunHidden(Driver + '\devcon.exe', 'install "' + Driver + '\MttVDD.inf" ' + DriverHardwareId, Code) and ((Code = 0) or (Code = 1)) then
  begin
    RegWriteDWordValue(HKLM, StateKey, 'InstalledDriver', 1);
    DriverNeedsRestart := Code = 1;
  end
  else if not WizardSilent() then
    MsgBox('The virtual display driver could not be installed (code ' + IntToStr(Code) + ').' + #13#10#13#10 +
      'ImmersiveSBS is installed. Run the installer again, or install the Virtual Display Driver from its website.',
      mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallDriver();
end;

function NeedRestart(): Boolean;
begin
  Result := DriverNeedsRestart;
end;

{ Removes what the installer added, asking first about the driver and the user's data. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Flag: Cardinal;
  Code: Integer;
  Data: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  RegDeleteValue(HKCU, RunKey, 'ImmersiveSBS');

  if RegQueryDWordValue(HKLM, StateKey, 'InstalledDriver', Flag) and (Flag = 1) and not UninstallSilent() then
    if MsgBox('Also remove the virtual display driver that ImmersiveSBS installed?' + #13#10#13#10 +
        'Choose No if another program uses the virtual monitor.', mbConfirmation, MB_YESNO) = IDYES then
    begin
      RunHidden(ExpandConstant('{app}\driver\devcon.exe'), 'remove ' + DriverHardwareId, Code);
      if RegQueryDWordValue(HKLM, StateKey, 'AddedPublisher', Flag) and (Flag = 1) then
        RunHidden(ExpandConstant('{sys}\certutil.exe'), '-delstore TrustedPublisher ' + SignPathThumbprint, Code);
    end;
  RegDeleteKeyIncludingSubkeys(HKLM, StateKey);

  Data := ExpandConstant('{localappdata}\ImmersiveSBS');
  if DirExists(Data) and not UninstallSilent() then
    if MsgBox('Also delete your ImmersiveSBS settings, log and the models prepared for your GPU?' + #13#10#13#10 +
        'Model files you added yourself are kept.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DeleteFile(Data + '\settings.json');
      DeleteFile(Data + '\ImmersiveSBS.log');
      DelTree(Data + '\engines', True, True, True);
    end;
end;

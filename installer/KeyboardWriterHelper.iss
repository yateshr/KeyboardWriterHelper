; Setup for Keyboard Writer Helper (Inno Setup 6.5 or newer).
; Build it with installer\build-installer.cmd, which builds the exe, makes the
; pictures and texts (prepare.ps1) and runs ISCC.
;
; The portable version is the plain KeyboardWriterHelper.exe: no setup, no install.

#ifndef MyVersion
  #define MyVersion "0.0"
#endif
#ifndef AppExe
  #define AppExe "..\KeyboardWriterHelper.exe"
#endif
#define MyAppName "Keyboard Writer Helper"
#define MyAppExeName "KeyboardWriterHelper.exe"
#define MyAppURL "https://github.com/yateshr/KeyboardWriterHelper"

[Setup]
AppId={{6E0C3B8A-4F2D-4E7B-9A51-2C8D7F1B5E90}
AppName={#MyAppName}
AppVersion={#MyVersion}
AppVerName={#MyAppName} {#MyVersion}
AppPublisher=YATESH ROHIT
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
AppCopyright=Made by YATESH ROHIT
VersionInfoVersion={#MyVersion}.0.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} setup

; For this Windows account only: no administrator prompt, and the app can
; replace its own exe when it updates itself.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; The app holds this name while it runs; setup asks the user to close it.
#ifndef TestBuild
AppMutex=KeyboardWriterHelper.SingleInstance
#endif
CloseApplications=no

SetupIconFile=..\assets\icon.ico
WizardStyle=modern dynamic
WizardImageFile=build\wizard-large-100.bmp,build\wizard-large-125.bmp,build\wizard-large-150.bmp,build\wizard-large-175.bmp,build\wizard-large-200.bmp,build\wizard-large-250.bmp,build\wizard-large-300.bmp
WizardSmallImageFile=build\wizard-small-100.bmp,build\wizard-small-125.bmp,build\wizard-small-150.bmp,build\wizard-small-175.bmp,build\wizard-small-200.bmp,build\wizard-small-250.bmp,build\wizard-small-300.bmp
DisableWelcomePage=no
WizardImageFileDynamicDark=build\wizard-large-100.bmp,build\wizard-large-125.bmp,build\wizard-large-150.bmp,build\wizard-large-175.bmp,build\wizard-large-200.bmp,build\wizard-large-250.bmp,build\wizard-large-300.bmp
WizardSmallImageFileDynamicDark=build\wizard-small-100.bmp,build\wizard-small-125.bmp,build\wizard-small-150.bmp,build\wizard-small-175.bmp,build\wizard-small-200.bmp,build\wizard-small-250.bmp,build\wizard-small-300.bmp
InfoBeforeFile=before.txt
InfoAfterFile=after.txt

OutputDir=Output
OutputBaseFilename=KeyboardWriterHelper-Setup-{#MyVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#MyAppName} when I sign in to Windows"; GroupDescription: "While you use it:"
Name: "desktopicon"; Description: "Put an icon on the desktop"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; Setup draws its own pictures at the exact size they are shown (see DrawWizardArt), using
; the app itself. It is listed first so it comes out of the compressed data quickly.
Source: "{#AppExe}"; DestName: "art.exe"; Flags: dontcopy
Source: "{#AppExe}"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\changelog.txt"; Flags: dontcopy
Source: "build\credits.txt"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; The same entry the app's own "Start with Windows" switch writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "KeyboardWriterHelper"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Start {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  SettingsFolder = '\KeyboardWriterHelper';

var
  Jokes: array of String;

procedure AddJoke(const Text: String);
begin
  SetArrayLength(Jokes, GetArrayLength(Jokes) + 1);
  Jokes[GetArrayLength(Jokes) - 1] := Text;
end;

function TextOfFile(const Name: String): String;
var
  Raw: AnsiString;
begin
  ExtractTemporaryFile(Name);
  if LoadStringFromFile(ExpandConstant('{tmp}\' + Name), Raw) then
    Result := String(Raw)
  else
    Result := 'Could not read ' + Name + '.';
end;

{ Draws a picture at the exact pixel size of the control that shows it, so nothing is
  stretched and the vector drawing stays sharp at any Windows scaling. If it cannot be
  drawn, the bitmaps built into setup stay. }
procedure DrawArt(Image: TBitmapImage; const Kind: String; const BackColor: Integer);
var
  Picture: String;
  Code: Integer;
begin
  Picture := ExpandConstant('{tmp}\art-' + Kind + '-' + IntToStr(Image.Width) + 'x' + IntToStr(Image.Height) + '.bmp');
  if Exec(ExpandConstant('{tmp}\art.exe'),
          '--wizard-art "' + Picture + '" ' + Kind + ' ' + IntToStr(Image.Width) + ' ' + IntToStr(Image.Height) +
          ' {#MyVersion} ' + IntToStr(BackColor),
          '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) and FileExists(Picture) then
  begin
    Image.Stretch := False;
    Image.Bitmap.LoadFromFile(Picture);
    Log('Picture ' + Kind + ': drawn at ' + IntToStr(Image.Width) + 'x' + IntToStr(Image.Height) +
        ', loaded ' + IntToStr(Image.Bitmap.Width) + 'x' + IntToStr(Image.Bitmap.Height) +
        ', screen ' + IntToStr(WizardForm.Font.PixelsPerInch) + ' dpi');
  end
  else
    Log('Picture ' + Kind + ': not drawn (exit code ' + IntToStr(Code) + '), using the built-in one');
end;

function GetSysColor(Index: Integer): Integer; external 'GetSysColor@user32.dll stdcall';

{ The colour behind the small picture as red-green-blue; a Windows colour name is looked up. }
function PanelColor: Integer;
begin
  Result := WizardForm.MainPanel.Color;
  if Result < 0 then Result := GetSysColor(Result and $FF);
end;

procedure DrawWizardArt;
begin
  if WizardSilent then Exit;
  try
    ExtractTemporaryFile('art.exe');
    DrawArt(WizardForm.WizardBitmapImage, 'large', 0);
    DrawArt(WizardForm.WizardBitmapImage2, 'large', 0);
    DrawArt(WizardForm.WizardSmallBitmapImage, 'small', PanelColor);
  except
  end;
end;

procedure InitializeWizard;
var
  Changes, Credits: TOutputMsgMemoWizardPage;
begin
  DrawWizardArt;
  { A short one for the end. }
  AddJoke('It works on my machine. Now it works on yours too.');
  AddJoke('Deployed on a Friday. Brave. We salute you.');
  AddJoke('There are 10 kinds of people: those who read changelogs and those who just click Next.');
  AddJoke('Why do programmers prefer dark mode? Because light attracts bugs.');
  AddJoke('Rollback plan: turn it off and on again.');
  AddJoke('99 little bugs in the code. Take one down, patch it around... 127 little bugs in the code.');
  AddJoke('A SQL query walks into a bar, sees two tables and asks: "Can I join you?"');
  AddJoke('In case of fire: git commit, git push, leave the building.');
  AddJoke('Installation successful. Please do not test in production. (You just did.)');
  AddJoke('This app now fixes "teh" for you. You are welcome.');
  AddJoke('It is not a bug, it is an undocumented feature. (This one is documented.)');
  AddJoke('Ninety percent of coding is debugging. The other ten percent is writing the bugs.');

  Changes := CreateOutputMsgMemoPage(wpInfoBefore, 'What''s new',
    'The changes in this version and the ones before it.', 'Newest first.',
    TextOfFile('changelog.txt'));
  Credits := CreateOutputMsgMemoPage(Changes.ID, 'Credits',
    'The people and projects behind the word data.', 'Thank you to everyone who shared them.',
    TextOfFile('credits.txt'));
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Joke: String;
begin
  if CurPageID = wpWelcome then
    WizardForm.WelcomeLabel2.Caption :=
      'This installs {#MyAppName} {#MyVersion} for your Windows account.' + #13#10#13#10 +
      'It fixes spelling mistakes in any program as you type, and everything stays on this PC.' + #13#10#13#10 +
      'Click Next to continue, or Cancel to leave it all alone.'
  else if CurPageID = wpInfoBefore then
  begin
    WizardForm.PageNameLabel.Caption := 'Before you install';
    WizardForm.PageDescriptionLabel.Caption := 'What this setup does, and what it does not do.';
  end
  else if CurPageID = wpInfoAfter then
  begin
    WizardForm.PageNameLabel.Caption := 'You are all set';
    WizardForm.PageDescriptionLabel.Caption := 'How to find it and the few things worth knowing.';
  end
  else if CurPageID = wpFinished then
  begin
    Joke := Jokes[StrToIntDef(GetDateTimeString('ss', #0, #0), 0) mod GetArrayLength(Jokes)];
    WizardForm.FinishedLabel.Caption :=
      '{#MyAppName} {#MyVersion} is installed.' + #13#10#13#10 + Joke;
    WizardForm.FinishedLabel.AdjustHeight;
    if WizardForm.RunList.Visible then
    begin
      WizardForm.RunList.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(24);
      WizardForm.RunList.Height := ScaleY(40);
    end;
  end;
end;

{ Take the startup entry away only if it points at the copy being removed. }
procedure RemoveStartupEntry;
var
  Value: String;
begin
  if RegQueryStringValue(HKCU, RunKey, 'KeyboardWriterHelper', Value) then
    if Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Value)) > 0 then
      RegDeleteValue(HKCU, RunKey, 'KeyboardWriterHelper');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveStartupEntry
  else if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
      if MsgBox('Also delete your settings, My words and My fixes?' + #13#10#13#10 +
                'Choose No to keep them for a later install.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}') + SettingsFolder, True, True, True);
  end;
end;

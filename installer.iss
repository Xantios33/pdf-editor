[Setup]
AppName=PDF Editor
AppVersion=1.0.0
AppPublisher=Xantios33
AppPublisherURL=https://github.com/Xantios33/pdf-editor
DefaultDirName={autopf}\PDF Editor
DefaultGroupName=PDF Editor
OutputDir=installer_output
OutputBaseFilename=PDFEditor_Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayName=PDF Editor
SetupIconFile=icone.ico
UninstallDisplayIcon={app}\icone.ico

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
Source: "src\PdfEditor.App\bin\Release\net8.0-windows10.0.22621.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "icone.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PDF Editor"; Filename: "{app}\PdfEditor.App.exe"; IconFilename: "{app}\icone.ico"
Name: "{group}\Desinstaller PDF Editor"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PDF Editor"; Filename: "{app}\PdfEditor.App.exe"; IconFilename: "{app}\icone.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Creer un raccourci sur le bureau"; GroupDescription: "Raccourcis:"; Flags: checkedonce

[Run]
Filename: "{app}\PdfEditor.App.exe"; Description: "Lancer PDF Editor"; Flags: nowait postinstall skipifsilent

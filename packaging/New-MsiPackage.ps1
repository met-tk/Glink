[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$work = Join-Path $PSScriptRoot ('.build\msi-' + $(if ($SmokeTest) { 'test' } else { 'release' }))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$productName = if ($SmokeTest) { 'Glink Installer Test' } else { 'Glink' }
$suffix = if ($SmokeTest) { 'Installer-Test' } else { 'Setup' }
$output = Join-Path $OutputDirectory "Glink-$Version-$suffix-x64.msi"
$upgradeCode = if ($SmokeTest) { '{9361A931-CCB3-4C8C-94F8-B97275418D5D}' } else { '{3DFCB639-0288-441C-A539-8FA78FE68191}' }
$makecab = Join-Path $env:SystemRoot 'System32\makecab.exe'
foreach ($tool in @($makecab)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $tool
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
        throw "The Microsoft tool signature is not valid: $tool"
    }
}

function Get-StableId([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value.ToLowerInvariant())))).Replace('-', '') }
    finally { $sha.Dispose() }
}
function Get-StableGuid([string]$value) {
    $hash = Get-StableId $value
    return '{' + $hash.Substring(0,8) + '-' + $hash.Substring(8,4) + '-4' + $hash.Substring(13,3) + '-A' + $hash.Substring(17,3) + '-' + $hash.Substring(20,12) + '}'
}
$productCode = Get-StableGuid "$upgradeCode/product/$Version/x64"
$dataRoot = if ($SmokeTest) {
    # Keep fixtures outside MSIX-host AppData virtualization so MSI sees the same files.
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.build\smoke\user-data')) + '\'
} else { '[LocalAppDataFolder]Glink\' }
$manifest = [Collections.Generic.List[object]]::new()
$index = 0
foreach ($file in Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | Sort-Object FullName) {
    $index++
    $relative = $file.FullName.Substring($PublishDirectory.Length + 1)
    if ($relative -match '[\t\r\n"]') { throw "Unsupported payload filename: $relative" }
    if ($file.Extension -match '^\.(ps1|bat|cmd|msi|pdb|db)$') { throw "Unexpected release payload file: $relative" }
    $id = (Get-StableId $relative).Substring(0,32)
    $extension = $file.Extension.TrimStart('.').ToUpperInvariant()
    if ($extension -notmatch '^[A-Z0-9]{1,3}$') { $extension = 'DAT' }
    $short = 'F{0:D6}.{1}' -f $index, $extension
    $manifest.Add([pscustomobject]@{ File = $file; Relative = $relative; Id = $id; Sequence = $index; ShortName = $short })
}
if ($manifest.Count -eq 0) { throw 'Cannot build an empty installer.' }

# The cabinet contains only the published application. No downloaded bootstrapper or install-time scripts.
$ddf = [Collections.Generic.List[string]]::new()
$ddf.Add('.OPTION EXPLICIT')
$ddf.Add('.Set Cabinet=on')
$ddf.Add('.Set Compress=on')
$ddf.Add('.Set CompressionType=MSZIP')
$ddf.Add('.Set MaxDiskSize=0')
$ddf.Add('.Set CabinetNameTemplate=payload.cab')
$ddf.Add('.Set DiskDirectoryTemplate="' + $work + '"')
$ddf.Add('.Set RptFileName="' + (Join-Path $work 'cabinet.rpt') + '"')
$ddf.Add('.Set InfFileName="' + (Join-Path $work 'cabinet.inf') + '"')
foreach ($item in $manifest) { $ddf.Add('"' + $item.File.FullName + '" f' + $item.Id) }
$ddfPath = Join-Path $work 'payload.ddf'
[IO.File]::WriteAllLines($ddfPath, $ddf, [Text.Encoding]::Default)
& $makecab /F $ddfPath > (Join-Path $work 'makecab.log')
if ($LASTEXITCODE -ne 0) { throw "makecab failed; see $work\makecab.log" }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($output, 3)
[IO.File]::WriteAllText((Join-Path $work 'codepage.idt'), "`r`n`r`n936`t_ForceCodepage`r`n", [Text.Encoding]::ASCII)
$database.Import($work, 'codepage.idt')
function Invoke-MsiSql([string]$sql) {
    $view = $database.OpenView($sql)
    try { $view.Execute() } finally { $view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
}
function Add-MsiRow([string]$table, [string[]]$columns, [object[]]$values) {
    $quotedColumns = ($columns | ForEach-Object { '`' + $_ + '`' }) -join ', '
    $parameters = (@('?') * $values.Count) -join ', '
    $view = $database.OpenView('INSERT INTO `' + $table + '` (' + $quotedColumns + ') VALUES (' + $parameters + ')')
    $record = $installer.CreateRecord($values.Count)
    for ($n=0; $n -lt $values.Count; $n++) {
        if ($null -eq $values[$n]) { continue }
        if ($values[$n] -is [int]) { $record.IntegerData($n + 1) = $values[$n] }
        else { $record.StringData($n + 1) = [string]$values[$n] }
    }
    try { $view.Execute($record) }
    finally {
        $view.Close()
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
    }
}

try {
    $tables = @(
        'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)',
        'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)',
        'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)',
        'CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)',
        'CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)',
        'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)',
        'CREATE TABLE `MsiFileHash` (`File_` CHAR(72) NOT NULL, `Options` SHORT NOT NULL, `HashPart1` LONG NOT NULL, `HashPart2` LONG NOT NULL, `HashPart3` LONG NOT NULL, `HashPart4` LONG NOT NULL PRIMARY KEY `File_`)',
        'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)',
        'CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)',
        'CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(255) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)',
        'CREATE TABLE `RemoveFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) LOCALIZABLE, `DirProperty` CHAR(72) NOT NULL, `InstallMode` SHORT NOT NULL PRIMARY KEY `FileKey`)',
        'CREATE TABLE `CreateFolder` (`Directory_` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Directory_`, `Component_`)',
        'CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)',
        'CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)',
        'CREATE TABLE `AppSearch` (`Property` CHAR(72) NOT NULL, `Signature_` CHAR(72) NOT NULL PRIMARY KEY `Property`, `Signature_`)',
        'CREATE TABLE `RegLocator` (`Signature_` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL, `Name` CHAR(255), `Type` SHORT PRIMARY KEY `Signature_`)',
        'CREATE TABLE `Signature` (`Signature` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `MinVersion` CHAR(20), `MaxVersion` CHAR(20), `MinSize` LONG, `MaxSize` LONG, `MinDate` LONG, `MaxDate` LONG, `Languages` CHAR(255) PRIMARY KEY `Signature`)',
        'CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)',
        'CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)',
        'CREATE TABLE `Dialog` (`Dialog` CHAR(72) NOT NULL, `HCentering` SHORT NOT NULL, `VCentering` SHORT NOT NULL, `Width` SHORT NOT NULL, `Height` SHORT NOT NULL, `Attributes` LONG NOT NULL, `Title` CHAR(128) LOCALIZABLE, `Control_First` CHAR(50) NOT NULL, `Control_Default` CHAR(50), `Control_Cancel` CHAR(50) PRIMARY KEY `Dialog`)',
        'CREATE TABLE `Control` (`Dialog_` CHAR(72) NOT NULL, `Control` CHAR(50) NOT NULL, `Type` CHAR(20) NOT NULL, `X` SHORT NOT NULL, `Y` SHORT NOT NULL, `Width` SHORT NOT NULL, `Height` SHORT NOT NULL, `Attributes` LONG, `Property` CHAR(72), `Text` CHAR(0) LOCALIZABLE, `Control_Next` CHAR(50), `Help` CHAR(50) LOCALIZABLE PRIMARY KEY `Dialog_`, `Control`)',
        'CREATE TABLE `ControlEvent` (`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Event` CHAR(50) NOT NULL, `Argument` CHAR(255) NOT NULL, `Condition` CHAR(255), `Ordering` SHORT PRIMARY KEY `Dialog_`, `Control_`, `Event`, `Argument`, `Condition`)',
        'CREATE TABLE `ControlCondition` (`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Action` CHAR(50) NOT NULL, `Condition` CHAR(255) NOT NULL PRIMARY KEY `Dialog_`, `Control_`, `Action`, `Condition`)',
        'CREATE TABLE `CheckBox` (`Property` CHAR(72) NOT NULL, `Value` CHAR(64) PRIMARY KEY `Property`)',
        'CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(255) PRIMARY KEY `Action`)',
        'CREATE TABLE `ListBox` (`Property` CHAR(72) NOT NULL, `Order` SHORT NOT NULL, `Value` CHAR(64) NOT NULL, `Text` CHAR(64) LOCALIZABLE PRIMARY KEY `Property`, `Order`)',
        'CREATE TABLE `EventMapping` (`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Event` CHAR(50) NOT NULL, `Attribute` CHAR(50) NOT NULL PRIMARY KEY `Dialog_`, `Control_`, `Event`)',
        'CREATE TABLE `TextStyle` (`TextStyle` CHAR(72) NOT NULL, `FaceName` CHAR(32) NOT NULL, `Size` SHORT NOT NULL, `Color` LONG, `StyleBits` SHORT PRIMARY KEY `TextStyle`)',
        'CREATE TABLE `ActionText` (`Action` CHAR(72) NOT NULL, `Description` CHAR(64) LOCALIZABLE, `Template` CHAR(128) LOCALIZABLE PRIMARY KEY `Action`)',
        'CREATE TABLE `Icon` (`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)'
    )
    foreach ($sql in $tables) { Invoke-MsiSql $sql }
    $properties = [ordered]@{
        ProductCode = $productCode; UpgradeCode = $upgradeCode; ProductName = $productName; ProductVersion = $Version
        Manufacturer = 'AI-driven by taketo'; ProductLanguage = '2052'; INSTALLLEVEL = '1'
        ARPCONTACT = 'AI-driven by taketo'; ARPURLINFOABOUT = 'https://space.bilibili.com/24340298'
        ARPHELPLINK = 'https://space.bilibili.com/24340298'; ARPPRODUCTICON = 'GlinkIcon'; ARPNOMODIFY = '1'
        ARPCOMMENTS = '卸载默认保留资料，也可在卸载时选择删除默认灵感库、媒体与设置。'
        SecureCustomProperties = 'OLDPRODUCTS;NEWERPRODUCTFOUND;INSTALLDIR;PREVIOUSINSTALLDIR;REMOVEUSERDATA'; DefaultUIFont = 'Normal'
        MSIRESTARTMANAGERCONTROL = 'DisableShutdown'; REBOOT = 'ReallySuppress'
    }
    foreach ($entry in $properties.GetEnumerator()) { Add-MsiRow Property @('Property','Value') @($entry.Key, $entry.Value) }
    $directoryColumns = @('Directory','Directory_Parent','DefaultDir')
    Add-MsiRow Directory $directoryColumns @('TARGETDIR',$null,'SourceDir')
    Add-MsiRow Directory $directoryColumns @('LocalAppDataFolder','TARGETDIR','.')
    Add-MsiRow Directory $directoryColumns @('ProgramsFolder','LocalAppDataFolder','Programs')
    Add-MsiRow Directory $directoryColumns @('INSTALLDIR','ProgramsFolder',$(if($SmokeTest){'GLINKT~1|Glink Installer Test'}else{'Glink'}))
    Add-MsiRow Directory $directoryColumns @('ProgramMenuFolder','TARGETDIR','.')
    Add-MsiRow Directory $directoryColumns @('AppMenuFolder','ProgramMenuFolder',$(if($SmokeTest){'GLINKT~1|Glink Installer Test'}else{'Glink'}))
    Add-MsiRow Directory $directoryColumns @('DesktopFolder','TARGETDIR','.')
    $directoryMap = @{ '' = 'INSTALLDIR' }
    $directoryNumber = 0
    foreach ($item in $manifest) {
        $relativeDirectory = [IO.Path]::GetDirectoryName($item.Relative)
        $parent = ''
        foreach ($part in $relativeDirectory.Split('\', [StringSplitOptions]::RemoveEmptyEntries)) {
            $current = if ($parent) { $parent + '\' + $part } else { $part }
            if (!$directoryMap.ContainsKey($current)) {
                $directoryNumber++
                $directoryId = 'd' + (Get-StableId $current).Substring(0,32)
                $shortDirectory = 'D{0:D6}|{1}' -f $directoryNumber, $part
                Add-MsiRow Directory $directoryColumns @($directoryId,$directoryMap[$parent],$shortDirectory)
                $directoryMap[$current] = $directoryId
            }
            $parent = $current
        }
    }
    Add-MsiRow Feature @('Feature','Title','Description','Display','Level','Directory_','Attributes') @('Main','Glink','应用程序和运行时',1,1,'INSTALLDIR',0)
    $componentColumns = @('Component','ComponentId','Directory_','Attributes','KeyPath')
    foreach ($item in $manifest) {
        $fileId = 'f' + $item.Id
        $componentId = 'c' + $item.Id
        $registryId = 'r' + $item.Id
        $directory = $directoryMap[[IO.Path]::GetDirectoryName($item.Relative)]
        Add-MsiRow Component $componentColumns @($componentId,(Get-StableGuid "$upgradeCode/component/x64/$($item.Relative)"),$directory,260,$registryId)
        Add-MsiRow FeatureComponents @('Feature_','Component_') @('Main',$componentId)
        Add-MsiRow Registry @('Registry','Root','Key','Name','Value','Component_') @($registryId,1,"Software\$productName\Installer\Components",$item.Id,$Version,$componentId)
        $fileVersion = $installer.FileVersion($item.File.FullName, $false)
        $fileLanguage = $installer.FileVersion($item.File.FullName, $true)
        Add-MsiRow File @('File','Component_','FileName','FileSize','Version','Language','Attributes','Sequence') @($fileId,$componentId,($item.ShortName + '|' + $item.File.Name),[int]$item.File.Length,$fileVersion,$fileLanguage,512,$item.Sequence)
        if (!$fileVersion) {
            $hash = $installer.FileHash($item.File.FullName,0)
            Add-MsiRow MsiFileHash @('File_','Options','HashPart1','HashPart2','HashPart3','HashPart4') @($fileId,0,$hash.IntegerData(1),$hash.IntegerData(2),$hash.IntegerData(3),$hash.IntegerData(4))
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($hash)
        }
    }
    $appFileId = 'f' + (Get-StableId 'Glink.exe').Substring(0,32)
    Add-MsiRow Component $componentColumns @('MenuComponent',(Get-StableGuid "$upgradeCode/shortcut/x64"),'AppMenuFolder',260,'MenuRegistry')
    Add-MsiRow FeatureComponents @('Feature_','Component_') @('Main','MenuComponent')
    Add-MsiRow Registry @('Registry','Root','Key','Name','Value','Component_') @('MenuRegistry',1,"Software\$productName\Installer",'InstalledVersion',$Version,'MenuComponent')
    Add-MsiRow Registry @('Registry','Root','Key','Name','Value','Component_') @('InstallPathRegistry',1,"Software\$productName\Installer",'InstalledPath','[INSTALLDIR]','MenuComponent')
    Add-MsiRow Shortcut @('Shortcut','Directory_','Name','Component_','Target','Description','Icon_','IconIndex','ShowCmd','WkDir') @('StartMenu','AppMenuFolder','Glink','MenuComponent',('[#' + $appFileId + ']'),'Glink 灵感库','GlinkIcon',0,1,'INSTALLDIR')
    # 桌面快捷方式按产品区分命名：冒烟测试与正式安装共存时互不覆盖。
    $desktopShortcutName = if ($SmokeTest) { 'GLINKT~1|Glink Installer Test' } else { 'Glink' }
    Add-MsiRow Shortcut @('Shortcut','Directory_','Name','Component_','Target','Description','Icon_','IconIndex','ShowCmd','WkDir') @('DesktopShortcut','DesktopFolder',$desktopShortcutName,'MenuComponent',('[#' + $appFileId + ']'),'Glink 灵感库','GlinkIcon',0,1,'INSTALLDIR')
    Add-MsiRow CreateFolder @('Directory_','Component_') @('AppMenuFolder','MenuComponent')
    Add-MsiRow RemoveFile @('FileKey','Component_','DirProperty','InstallMode') @('RemoveMenuFolder','MenuComponent','AppMenuFolder',2)
    $dataFiles = @('moodia.db','moodia.db-wal','moodia.db-shm','moodia.db-journal','appearance.txt','shortcuts.txt','window.txt','text-prefs.txt','layout-prefs.txt','layout-presets.json','category-order.txt','category-groups.txt','crash.log')
    $dataFileNumber = 0
    foreach ($dataFile in $dataFiles) {
        foreach ($name in @($dataFile,($dataFile + '.tmp'))) {
            $dataFileNumber++
            Add-MsiRow RemoveFile @('FileKey','Component_','FileName','DirProperty','InstallMode') @("DeleteData$dataFileNumber",'MenuComponent',$name,'GLINKCLEANUPROOT',2)
        }
    }
    foreach ($child in @('MEDIA','UNDO')) {
        Add-MsiRow RemoveFile @('FileKey','Component_','FileName','DirProperty','InstallMode') @("Delete${child}Files",'MenuComponent','*',"GLINKCLEANUP$child",2)
        Add-MsiRow RemoveFile @('FileKey','Component_','DirProperty','InstallMode') @("Delete${child}Folder",'MenuComponent',"GLINKCLEANUP$child",2)
    }
    Add-MsiRow RemoveFile @('FileKey','Component_','DirProperty','InstallMode') @('DeleteDataFolder','MenuComponent','GLINKCLEANUPROOT',2)
    Add-MsiRow Media @('DiskId','LastSequence','Cabinet') @(1,$manifest.Count,'#payload.cab')
    Add-MsiRow Upgrade @('UpgradeCode','VersionMin','VersionMax','Attributes','ActionProperty') @($upgradeCode,'0.0.0',$Version,513,'OLDPRODUCTS')
    Add-MsiRow Upgrade @('UpgradeCode','VersionMin','Attributes','ActionProperty') @($upgradeCode,$Version,2,'NEWERPRODUCTFOUND')
    Add-MsiRow AppSearch @('Property','Signature_') @('WINDOWSBUILDNUM','WindowsBuildNumber')
    Add-MsiRow RegLocator @('Signature_','Root','Key','Name','Type') @('WindowsBuildNumber',2,'SOFTWARE\Microsoft\Windows NT\CurrentVersion','CurrentBuildNumber',18)
    Add-MsiRow AppSearch @('Property','Signature_') @('PREVIOUSINSTALLDIR','PreviousInstallPath')
    Add-MsiRow RegLocator @('Signature_','Root','Key','Name','Type') @('PreviousInstallPath',1,"Software\$productName\Installer",'InstalledPath',18)
    Add-MsiRow CustomAction @('Action','Type','Source','Target') @('RestoreInstallDir',51,'INSTALLDIR','[PREVIOUSINSTALLDIR]')
    $cleanupCondition = 'Installed AND REMOVE="ALL" AND REMOVEUSERDATA=1 AND NOT REINSTALL AND NOT UPGRADINGPRODUCTCODE'
    $cleanupPaths = [ordered]@{
        ROOT = $dataRoot
        MEDIA = $dataRoot + 'media\'
        UNDO = $dataRoot + '.undo\'
    }
    $resetOrder = 70
    $setOrder = 80
    foreach ($entry in $cleanupPaths.GetEnumerator()) {
        Add-MsiRow CustomAction @('Action','Type','Source','Target') @(('ResetCleanup' + $entry.Key),51,('GLINKCLEANUP' + $entry.Key),$null)
        Add-MsiRow CustomAction @('Action','Type','Source','Target') @(('SetCleanup' + $entry.Key),51,('GLINKCLEANUP' + $entry.Key),$entry.Value)
        foreach ($sequence in @('InstallUISequence','InstallExecuteSequence')) {
            Add-MsiRow $sequence @('Action','Sequence') @(('ResetCleanup' + $entry.Key),$resetOrder)
        }
        $resetOrder++
        Add-MsiRow InstallExecuteSequence @('Action','Condition','Sequence') @(('SetCleanup' + $entry.Key),$cleanupCondition,$setOrder++)
    }
    Add-MsiRow LaunchCondition @('Condition','Description') @('Installed OR (VersionNT64 AND WINDOWSBUILDNUM >= 19045)','Glink 需要 Windows 10 22H2（19045）或更新的 64 位 Windows。')
    Add-MsiRow LaunchCondition @('Condition','Description') @('Installed OR NOT NEWERPRODUCTFOUND','已安装更新版本的 Glink，请使用已有版本或先卸载。卸载会保留您的资料。')

    $execute = [ordered]@{
        FindRelatedProducts=25; AppSearch=50; LaunchConditions=100; CostInitialize=800; FileCost=900; CostFinalize=1000
        MigrateFeatureStates=1200; InstallValidate=1400; InstallInitialize=1500; RemoveExistingProducts=1510
        ProcessComponents=1600; UnpublishFeatures=1800; RemoveRegistryValues=2600; RemoveShortcuts=3200
        RemoveFiles=3500; RemoveFolders=3600; CreateFolders=3700; InstallFiles=4000; CreateShortcuts=4500
        WriteRegistryValues=5000; RegisterUser=6000; RegisterProduct=6100; PublishFeatures=6300
        PublishProduct=6400; InstallFinalize=6600
    }
    foreach ($entry in $execute.GetEnumerator()) { Add-MsiRow InstallExecuteSequence @('Action','Sequence') @($entry.Key,$entry.Value) }
    $ui = [ordered]@{ FindRelatedProducts=25; AppSearch=50; LaunchConditions=100; CostInitialize=800; FileCost=900; CostFinalize=1000; MigrateFeatureStates=1200; ExecuteAction=1300 }
    foreach ($entry in $ui.GetEnumerator()) { Add-MsiRow InstallUISequence @('Action','Sequence') @($entry.Key,$entry.Value) }
    foreach ($sequence in @('InstallUISequence','InstallExecuteSequence')) {
        Add-MsiRow $sequence @('Action','Condition','Sequence') @('RestoreInstallDir','PREVIOUSINSTALLDIR AND (Installed OR OLDPRODUCTS OR NOT INSTALLDIR)',60)
    }
    Add-MsiRow InstallUISequence @('Action','Condition','Sequence') @('WelcomeDlg','NOT Installed',1230)
    Add-MsiRow InstallUISequence @('Action','Condition','Sequence') @('UninstallDlg','Installed AND REMOVE~="ALL"',1240)
    Add-MsiRow InstallUISequence @('Action','Condition','Sequence') @('MaintenanceDlg','Installed AND NOT RESUME AND NOT Preselected AND NOT REMOVE~="ALL"',1250)
    Add-MsiRow InstallUISequence @('Action','Sequence') @('ProgressDlg',1280)
    Add-MsiRow InstallUISequence @('Action','Sequence') @('ExitDlg',-1)
    Add-MsiRow InstallUISequence @('Action','Sequence') @('UserExitDlg',-2)
    Add-MsiRow InstallUISequence @('Action','Sequence') @('FatalDlg',-3)
    Add-MsiRow TextStyle @('TextStyle','FaceName','Size','StyleBits') @('Normal','Microsoft YaHei UI',9,0)
    Add-MsiRow TextStyle @('TextStyle','FaceName','Size','StyleBits') @('Title','Microsoft YaHei UI',14,1)
    function Add-Dialog([string]$name, [string]$first, [string]$cancel, [int]$attributes=3, [int]$height=240) {
        Add-MsiRow Dialog @('Dialog','HCentering','VCentering','Width','Height','Attributes','Title','Control_First','Control_Default','Control_Cancel') @($name,50,50,370,$height,$attributes,'[ProductName] [ProductVersion]',$first,$first,$cancel)
    }
    function Add-Control([string]$dialog,[string]$id,[string]$type,[int]$x,[int]$y,[int]$width,[int]$height,[string]$text,[string]$next=$null) {
        Add-MsiRow Control @('Dialog_','Control','Type','X','Y','Width','Height','Attributes','Text','Control_Next') @($dialog,$id,$type,$x,$y,$width,$height,3,$text,$next)
    }
    function Add-Event([string]$dialog,[string]$control,[string]$event,[string]$argument,[int]$order=1) {
        Add-MsiRow ControlEvent @('Dialog_','Control_','Event','Argument','Condition','Ordering') @($dialog,$control,$event,$argument,'1',$order)
    }
    Add-Dialog WelcomeDlg Install Cancel
    Add-Control WelcomeDlg Title Text 24 22 322 30 '{\Title}安装 Glink'
    Add-Control WelcomeDlg Intro Text 24 62 322 34 '为当前用户安装。请选择当前账户可写的文件夹，并先关闭 Glink。'
    Add-Control WelcomeDlg PathLabel Text 24 100 322 15 '安装位置（升级时沿用现有位置）：'
    Add-MsiRow Control @('Dialog_','Control','Type','X','Y','Width','Height','Attributes','Property','Control_Next') @('WelcomeDlg','Path','PathEdit',24,119,244,20,3,'INSTALLDIR','Browse')
    Add-Control WelcomeDlg Browse PushButton 278 119 68 20 '浏览…(&B)' Install
    Add-Control WelcomeDlg Data Text 24 155 322 36 '升级和修复保留资料。卸载时可选择是否同时删除默认灵感库、媒体和设置。'
    Add-Control WelcomeDlg Install PushButton 192 207 72 20 '安装(&I)' Cancel
    Add-Control WelcomeDlg Cancel PushButton 274 207 72 20 '取消' Path
    Add-Event WelcomeDlg Browse '[BROWSESTARTPATH]' '[INSTALLDIR]'
    Add-Event WelcomeDlg Browse SpawnDialog BrowseDlg 2
    Add-Event WelcomeDlg Install SetTargetPath INSTALLDIR
    Add-Event WelcomeDlg Install EndDialog Return 2
    Add-Event WelcomeDlg Cancel EndDialog Exit
    foreach ($control in @('Path','Browse')) {
        Add-MsiRow ControlCondition @('Dialog_','Control_','Action','Condition') @('WelcomeDlg',$control,'Disable','PREVIOUSINSTALLDIR AND OLDPRODUCTS')
    }
    Add-Dialog BrowseDlg Choose Cancel 3 300
    Add-Control BrowseDlg Title Text 24 18 322 28 '{\Title}选择安装文件夹'
    Add-MsiRow Control @('Dialog_','Control','Type','X','Y','Width','Height','Attributes','Property','Control_Next') @('BrowseDlg','Drives','DirectoryCombo',24,56,244,80,393219,'INSTALLDIR','Folders')
    Add-Control BrowseDlg Up PushButton 278 56 68 20 '上一级' NewFolder
    Add-Control BrowseDlg NewFolder PushButton 278 83 68 20 '新建文件夹' Path
    Add-MsiRow Control @('Dialog_','Control','Type','X','Y','Width','Height','Attributes','Property','Control_Next') @('BrowseDlg','Folders','DirectoryList',24,84,244,140,3,'INSTALLDIR','Up')
    Add-MsiRow Control @('Dialog_','Control','Type','X','Y','Width','Height','Attributes','Property','Control_Next') @('BrowseDlg','Path','PathEdit',24,234,322,20,3,'INSTALLDIR','Choose')
    Add-Control BrowseDlg Choose PushButton 192 267 72 20 '选择' Cancel
    Add-Control BrowseDlg Cancel PushButton 274 267 72 20 '取消' Drives
    Add-Event BrowseDlg Up DirectoryListUp 0
    Add-Event BrowseDlg NewFolder DirectoryListNew 0
    Add-Event BrowseDlg Choose SetTargetPath INSTALLDIR
    Add-Event BrowseDlg Choose EndDialog Return 2
    Add-Event BrowseDlg Cancel '[INSTALLDIR]' '[BROWSESTARTPATH]'
    Add-Event BrowseDlg Cancel SetTargetPath INSTALLDIR 2
    Add-Event BrowseDlg Cancel EndDialog Return 3
    Add-Dialog MaintenanceDlg Repair Cancel
    Add-Control MaintenanceDlg Title Text 24 22 322 30 '{\Title}维护 Glink'
    Add-Control MaintenanceDlg Body Text 24 76 322 75 '修复可以重新安装程序文件，并保留资料。卸载时可选择是否同时删除默认灵感库、媒体和设置。'
    Add-Control MaintenanceDlg Repair PushButton 110 207 72 20 '修复(&R)' Remove
    Add-Control MaintenanceDlg Remove PushButton 192 207 72 20 '卸载(&U)' Cancel
    Add-Control MaintenanceDlg Cancel PushButton 274 207 72 20 '取消' Repair
    Add-Event MaintenanceDlg Repair '[REINSTALL]' ALL
    Add-Event MaintenanceDlg Repair '[REINSTALLMODE]' amus 2
    Add-Event MaintenanceDlg Repair EndDialog Return 3
    Add-Event MaintenanceDlg Remove '[REMOVE]' ALL
    Add-Event MaintenanceDlg Remove NewDialog UninstallDlg 2
    Add-Event MaintenanceDlg Cancel EndDialog Exit
    Add-Dialog UninstallDlg Remove Cancel
    Add-Control UninstallDlg Title Text 24 22 322 30 '{\Title}卸载 Glink'
    Add-Control UninstallDlg Body Text 24 66 322 35 '将移除程序文件。默认保留您的资料，以便重新安装后继续使用。请先关闭 Glink。'
    Add-MsiRow CheckBox @('Property','Value') @('REMOVEUSERDATA','1')
    Add-MsiRow Control @('Dialog_','Control','Type','X','Y','Width','Height','Attributes','Property','Text','Control_Next') @('UninstallDlg','DeleteData','CheckBox',24,113,322,20,3,'REMOVEUSERDATA','同时删除灵感库、媒体和设置','Remove')
    Add-Control UninstallDlg DataPath Text 24 143 322 44 ("勾选后将永久删除下列位置中的资料，无法撤销：`r`n" + $dataRoot.TrimEnd('\'))
    Add-Control UninstallDlg Remove PushButton 192 207 72 20 '卸载(&U)' Cancel
    Add-Control UninstallDlg Cancel PushButton 274 207 72 20 '取消' DeleteData
    Add-Event UninstallDlg Remove EndDialog Return
    Add-Event UninstallDlg Cancel EndDialog Exit
    Add-Dialog ProgressDlg Cancel Cancel 1
    Add-Control ProgressDlg Title Text 24 22 322 30 '{\Title}正在处理'
    Add-Control ProgressDlg Status Text 24 85 322 30 '正在安装或移除程序文件，请稍候。'
    Add-Control ProgressDlg Progress ProgressBar 24 132 322 15 ''
    Add-Control ProgressDlg Cancel PushButton 274 207 72 20 '取消' Cancel
    Add-MsiRow EventMapping @('Dialog_','Control_','Event','Attribute') @('ProgressDlg','Status','ActionText','Text')
    Add-MsiRow EventMapping @('Dialog_','Control_','Event','Attribute') @('ProgressDlg','Progress','SetProgress','Progress')
    Add-Event ProgressDlg Cancel SpawnDialog CancelDlg
    Add-Dialog CancelDlg No No
    Add-Control CancelDlg Title Text 24 22 322 30 '{\Title}取消当前操作？'
    Add-Control CancelDlg Body Text 24 80 322 60 'Windows Installer 将撤销尚未完成的更改。'
    Add-Control CancelDlg Yes PushButton 192 207 72 20 '是' No
    Add-Control CancelDlg No PushButton 274 207 72 20 '否' Yes
    Add-Event CancelDlg Yes EndDialog Exit
    Add-Event CancelDlg No EndDialog Return
    Add-Dialog FilesInUse Retry Cancel
    Add-Control FilesInUse Title Text 24 22 322 30 '{\Title}请先关闭以下程序'
    Add-Control FilesInUse Body Text 24 67 322 38 '部分程序文件正在使用中。请保存工作并关闭这些程序，然后点击重试。'
    Add-MsiRow Control @('Dialog_','Control','Type','X','Y','Width','Height','Attributes','Property') @('FilesInUse','AppList','ListBox',24,108,322,82,7,'FileInUseProcess')
    Add-Control FilesInUse Retry PushButton 192 207 72 20 '重试(&R)' Cancel
    Add-Control FilesInUse Cancel PushButton 274 207 72 20 '取消' Retry
    Add-Event FilesInUse Retry EndDialog Retry
    Add-Event FilesInUse Cancel EndDialog Exit
    foreach ($dialog in @(
        @('ExitDlg','操作完成','Glink [ProductVersion] 的操作已完成。安装后可从开始菜单打开 Glink。'),
        @('UserExitDlg','操作已取消','安装未完成，已有资料保持不变。'),
        @('FatalDlg','无法完成操作','Windows Installer 无法完成操作。请查看安装日志或系统提示后重试。')
    )) {
        Add-Dialog $dialog[0] Finish Finish
        Add-Control $dialog[0] Title Text 24 22 322 30 ('{\Title}' + $dialog[1])
        Add-Control $dialog[0] Body Text 24 78 322 95 $dialog[2]
        Add-Control $dialog[0] Finish PushButton 274 207 72 20 '完成' Finish
        Add-Event $dialog[0] Finish EndDialog Return
    }
    foreach ($action in @(@('InstallFiles','正在安装文件','[1]'),@('RemoveFiles','正在移除程序文件','[1]'),@('CreateShortcuts','正在创建快捷方式','[1]'),@('RegisterProduct','正在注册应用',''),@('InstallFinalize','正在完成操作',''))) {
        Add-MsiRow ActionText @('Action','Description','Template') $action
    }
    $iconView = $database.OpenView('INSERT INTO `Icon` (`Name`, `Data`) VALUES (?, ?)')
    $iconRecord = $installer.CreateRecord(2)
    $iconRecord.StringData(1) = 'GlinkIcon'
    $iconRecord.SetStream(2, (Join-Path (Split-Path $PSScriptRoot -Parent) 'Glink\Assets\AppIcon.ico'))
    $iconView.Execute($iconRecord)
    $iconView.Close()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($iconView)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($iconRecord)
    $cabinetView = $database.OpenView('INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)')
    $cabinetRecord = $installer.CreateRecord(2)
    $cabinetRecord.StringData(1) = 'payload.cab'
    $cabinetRecord.SetStream(2, (Join-Path $work 'payload.cab'))
    $cabinetView.Execute($cabinetRecord)
    $cabinetView.Close()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($cabinetView)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($cabinetRecord)
    $summary = $database.SummaryInformation(20)
    $summary.Property(1) = 936
    $summary.Property(2) = 'Installation Database'
    $summary.Property(3) = "$productName $Version"
    $summary.Property(4) = 'AI-driven by taketo'
    $summary.Property(5) = 'Installer, Glink'
    $summary.Property(6) = 'Per-user installation; uninstall preserves data unless removal is explicitly selected.'
    $summary.Property(7) = 'x64;2052'
    $summary.Property(9) = '{' + [guid]::NewGuid().ToString().ToUpperInvariant() + '}'
    $summary.Property(14) = 500
    $summary.Property(15) = 10 # Compressed sources; no elevated privileges required.
    $summary.Property(18) = 'Glink offline Windows Installer build'
    $summary.Property(19) = 2
    $summary.Persist()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary)
    $database.Commit()
}
finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}

$manifest | Select-Object Relative,Sequence,@{Name='Length';Expression={$_.File.Length}} | ConvertTo-Json -Depth 2 | Set-Content -LiteralPath (Join-Path $work 'payload-manifest.json') -Encoding utf8
@{ ProductCode=$productCode; UpgradeCode=$upgradeCode; Version=$Version; ProductName=$productName; FileCount=$manifest.Count; Installer=$output } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'package-info.json') -Encoding utf8
Write-Output $output

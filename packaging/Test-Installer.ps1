[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not ('GlinkInstallerTestContext' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class GlinkInstallerTestContext {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandle(System.IntPtr handle, System.Text.StringBuilder path, uint length, uint flags);
}
'@
}
$root = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path $PSScriptRoot '.build\smoke'
$payload = Join-Path $PSScriptRoot '.build\publish'
$info = Get-Content -LiteralPath (Join-Path $PSScriptRoot '.build\msi-test\package-info.json') -Raw | ConvertFrom-Json
if ($info.ProductName -ne 'Glink Installer Test' -or $info.UpgradeCode -ne '{9361A931-CCB3-4C8C-94F8-B97275418D5D}') {
    throw 'Refusing to test anything except the isolated Glink Installer Test product.'
}
$installDirectory = Join-Path $testRoot 'custom-install\Glink'
$wrongInstallDirectory = Join-Path $testRoot 'must-not-install-here'
$dataDirectory = Join-Path $testRoot 'user-data'
$decoyDirectory = Join-Path $testRoot 'must-not-delete'
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glink Installer Test'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Glink Installer Test.lnk'
$fixtureMarker = Join-Path $dataDirectory '.glink-installer-test-fixture'
$fixtureIdentity = 'Glink installer smoke fixture 9361A931-CCB3-4C8C-94F8-B97275418D5D'
if (Test-Path -LiteralPath $dataDirectory) {
    if (!(Test-Path -LiteralPath $fixtureMarker) -or (Get-Content -LiteralPath $fixtureMarker -Raw).Trim() -ne $fixtureIdentity) {
        throw 'The test data directory already exists without this test fixture marker; refusing to alter it.'
    }
} else {
    New-Item -ItemType Directory -Path $dataDirectory | Out-Null
    $fixtureIdentity | Set-Content -LiteralPath $fixtureMarker -Encoding utf8
}
$markerStream = [IO.File]::OpenRead($fixtureMarker)
try {
    $physicalPath = [Text.StringBuilder]::new(32768)
    $pathLength = [GlinkInstallerTestContext]::GetFinalPathNameByHandle($markerStream.SafeFileHandle.DangerousGetHandle(), $physicalPath, 32768, 0)
    if ($pathLength -eq 0 -or $pathLength -ge 32768 -or $physicalPath.ToString() -ne ('\\?\' + $fixtureMarker)) {
        throw 'The test fixture is redirected: its physical path differs from the directory Windows Installer uses.'
    }
}
finally { $markerStream.Dispose() }
foreach ($guardPath in @($dataDirectory,(Join-Path $dataDirectory 'media'),(Join-Path $dataDirectory '.undo'))) {
    if ((Test-Path -LiteralPath $guardPath) -and ((Get-Item -LiteralPath $guardPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The isolated test data root and known subfolders must not be links.'
    }
}
New-Item -ItemType Directory -Path $decoyDirectory -Force | Out-Null
$decoyFile = Join-Path $decoyDirectory 'moodia.db'
'An unrelated file must never be deleted.' | Set-Content -LiteralPath $decoyFile -Encoding utf8
$decoyHash = (Get-FileHash -LiteralPath $decoyFile).Hash
$sentinel = Join-Path $dataDirectory 'preserve-on-uninstall.txt'
'This isolated library must survive repair, upgrades and uninstall.' | Set-Content -LiteralPath $sentinel -Encoding utf8
$results = [Collections.Generic.List[string]]::new()
function Assert-Installer([bool]$condition, [string]$description) {
    $results.Add("$(if ($condition) { 'PASS' } else { 'FAIL' }) $description")
    $results | Set-Content -LiteralPath (Join-Path $testRoot 'installer-review.txt') -Encoding utf8
    if (!$condition) { throw $description }
}
function Invoke-Msi([string[]]$arguments, [string]$logName, [int]$expected=0) {
    $logPath = Join-Path $testRoot ($logName + '.log')
    $arguments += @('/qn','/norestart','/L*v',('"' + $logPath + '"'))
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(120000)) { throw "Windows Installer is still running; inspect $logPath before continuing." }
    Assert-Installer ($process.ExitCode -eq $expected) "$logName exits $expected (actual $($process.ExitCode))"
}
function Get-DataHashes {
    return @(Get-ChildItem -LiteralPath $dataDirectory -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
}
function Assert-DataPreserved([string[]]$expected,[string]$stage) {
    $actual = Get-DataHashes
    Assert-Installer (($expected -join "`n") -eq ($actual -join "`n")) "$stage preserves the complete isolated user library"
}
function Get-RegistrationPath([string]$productCode) {
    $code = $productCode.Trim('{}').Replace('-','')
    $packed = ''
    foreach ($part in @($code.Substring(0,8),$code.Substring(8,4),$code.Substring(12,4))) {
        $characters = $part.ToCharArray(); [Array]::Reverse($characters); $packed += -join $characters
    }
    for ($n=16; $n -lt 32; $n+=2) { $packed += [string]$code[$n+1] + [string]$code[$n] }
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$userSid\Products\$packed\InstallProperties"
}
function Assert-Payload([string]$stage) {
    $mismatches = @(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($payload.Length + 1)
        $installed = Join-Path $installDirectory $relative
        if (!(Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $installed).Hash) { $relative }
    })
    Assert-Installer ($mismatches.Count -eq 0) "${stage}: all $($info.FileCount) files match the published payload"
}

Invoke-Msi @('/i',('"' + $info.Installer + '"'),('INSTALLDIR="' + $installDirectory + '"')) 'install'
Assert-Installer (Test-Path -LiteralPath (Join-Path $installDirectory 'Glink.exe')) 'installs into the chosen application folder'
$savedInstallPath = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Glink Installer Test\Installer').InstalledPath
Assert-Installer ($savedInstallPath.TrimEnd('\') -eq $installDirectory) 'persists the chosen installation path for maintenance and upgrades'
$registration = Get-ItemProperty -LiteralPath (Get-RegistrationPath $info.ProductCode)
Assert-Installer ($registration.DisplayName -eq 'Glink Installer Test' -and $registration.WindowsInstaller -eq 1 -and $registration.UninstallString -eq ('MsiExec.exe /X' + $info.ProductCode)) 'registers standard per-user Windows Installer uninstall entry'
Assert-Installer (Test-Path -LiteralPath (Join-Path $startMenuDirectory 'Glink.lnk')) 'creates a Start menu shortcut'
Assert-Installer (Test-Path -LiteralPath $desktopShortcut) 'creates a Desktop shortcut'
Assert-Payload 'install'

$priorDataRoot = $env:GLINK_DATA_ROOT
try {
    $env:GLINK_DATA_ROOT = $dataDirectory
    $application = Start-Process -FilePath (Join-Path $installDirectory 'Glink.exe') -WorkingDirectory $installDirectory -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 6
    $application.Refresh()
    Assert-Installer (!$application.HasExited -and $application.MainWindowHandle -ne [IntPtr]::Zero) 'installed self-contained application opens a main window'
    Assert-Installer (Test-Path -LiteralPath (Join-Path $dataDirectory 'moodia.db')) 'installed application uses only the isolated test library'
    [void]$application.CloseMainWindow()
    Assert-Installer ($application.WaitForExit(20000)) 'installed application closes cleanly'
}
finally { $env:GLINK_DATA_ROOT = $priorDataRoot }
$dataBefore = Get-DataHashes
$repairTarget = Join-Path $installDirectory 'Glink.runtimeconfig.json'
Remove-Item -LiteralPath $repairTarget
Invoke-Msi @('/fa',$info.ProductCode,('INSTALLDIR="' + $wrongInstallDirectory + '"'),'REMOVEUSERDATA=1',('GLINKCLEANUPROOT="' + $decoyDirectory + '"')) 'repair'
Assert-Installer ((Get-FileHash -LiteralPath $repairTarget).Hash -eq (Get-FileHash -LiteralPath (Join-Path $payload 'Glink.runtimeconfig.json')).Hash) 'repair restores a missing runtime configuration'
Assert-DataPreserved $dataBefore 'repair'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $wrongInstallDirectory 'Glink.exe'))) 'repair ignores a conflicting installation path'

# A synthetic test-only product version exercises major upgrades without changing the release binary.
$baseVersion = [version]$info.Version
$upgradeVersion = '{0}.{1}.{2}' -f $baseVersion.Major, $baseVersion.Minor, ($baseVersion.Build + 1)
& (Join-Path $PSScriptRoot 'New-MsiPackage.ps1') -PublishDirectory $payload -OutputDirectory $testRoot -Version $upgradeVersion -SmokeTest | Out-Null
$upgradeInfo = Get-Content -LiteralPath (Join-Path $PSScriptRoot '.build\msi-test\package-info.json') -Raw | ConvertFrom-Json
Invoke-Msi @('/i',('"' + $upgradeInfo.Installer + '"'),('INSTALLDIR="' + $wrongInstallDirectory + '"'),'REMOVEUSERDATA=1',('GLINKCLEANUPROOT="' + $decoyDirectory + '"')) 'upgrade'
Assert-Installer (!(Test-Path -LiteralPath (Get-RegistrationPath $info.ProductCode))) 'upgrade removes the previous product registration'
$upgraded = Get-ItemProperty -LiteralPath (Get-RegistrationPath $upgradeInfo.ProductCode)
Assert-Installer ($upgraded.DisplayVersion -eq $upgradeVersion) 'upgrade registers the newer product version'
Assert-Payload 'upgrade'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $wrongInstallDirectory 'Glink.exe'))) 'upgrade preserves the existing chosen path despite a conflicting override'
Assert-DataPreserved $dataBefore 'upgrade'
Invoke-Msi @('/i',('"' + $info.Installer + '"')) 'downgrade-rejected' 1603
Assert-DataPreserved $dataBefore 'rejected downgrade'
Invoke-Msi @('/x',$upgradeInfo.ProductCode,('GLINKCLEANUPROOT="' + $decoyDirectory + '"')) 'uninstall-default'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $installDirectory 'Glink.exe'))) 'uninstall removes installed application files'
Assert-Installer (!(Test-Path -LiteralPath (Get-RegistrationPath $upgradeInfo.ProductCode))) 'uninstall removes the standard uninstall entry'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $startMenuDirectory 'Glink.lnk'))) 'uninstall removes the Start menu shortcut'
Assert-Installer (!(Test-Path -LiteralPath $desktopShortcut)) 'uninstall removes the Desktop shortcut'
Assert-DataPreserved $dataBefore 'uninstall'
Assert-Installer ((Get-FileHash -LiteralPath $decoyFile).Hash -eq $decoyHash) 'default uninstall ignores injected cleanup paths'

Invoke-Msi @('/i',('"' + $upgradeInfo.Installer + '"'),('INSTALLDIR="' + $installDirectory + '"')) 'reinstall-for-delete'
foreach ($child in @('media','.undo','unknown-subfolder','media\unknown-subfolder')) {
    $childDirectory = Join-Path $dataDirectory $child
    New-Item -ItemType Directory -Path $childDirectory -Force | Out-Null
    'Isolated fixture.' | Set-Content -LiteralPath (Join-Path $childDirectory 'fixture.bin') -Encoding utf8
}
'Isolated SQLite rollback fixture.' | Set-Content -LiteralPath (Join-Path $dataDirectory 'moodia.db-journal') -Encoding utf8
'Isolated settings fixture.' | Set-Content -LiteralPath (Join-Path $dataDirectory 'appearance.txt.tmp') -Encoding utf8
Invoke-Msi @('/x',$upgradeInfo.ProductCode,'REMOVEUSERDATA=1',('GLINKCLEANUPROOT="' + $decoyDirectory + '"'),('GLINKCLEANUPMEDIA="' + $decoyDirectory + '"'),('GLINKCLEANUPUNDO="' + $decoyDirectory + '"')) 'uninstall-delete-data'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $dataDirectory 'moodia.db'))) 'explicit opt-in removes the fixed test library database'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $dataDirectory 'moodia.db-journal'))) 'explicit opt-in removes the SQLite rollback journal'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $dataDirectory 'appearance.txt.tmp'))) 'explicit opt-in removes application settings temp files'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $dataDirectory 'media\fixture.bin')) -and !(Test-Path -LiteralPath (Join-Path $dataDirectory '.undo\fixture.bin'))) 'explicit opt-in removes only the two known data subfolders contents'
Assert-Installer (Test-Path -LiteralPath (Join-Path $dataDirectory 'unknown-subfolder\fixture.bin')) 'uninstall does not recurse into unknown data subfolders'
Assert-Installer (Test-Path -LiteralPath (Join-Path $dataDirectory 'media\unknown-subfolder\fixture.bin')) 'media wildcard cleanup does not recurse into nested folders'
Assert-Installer (Test-Path -LiteralPath $sentinel) 'uninstall preserves unknown user-added files'
Assert-Installer ((Get-FileHash -LiteralPath $decoyFile).Hash -eq $decoyHash) 'explicit deletion overrides all injected cleanup paths with the fixed test root'
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $installDirectory 'Glink.exe'))) 'explicit deletion also removes the application'
Write-Output ($results -join "`n")

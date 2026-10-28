[CmdletBinding()]
param(
    [string]$VcRuntimeDirectory,
    [switch]$SkipPublish,
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Glink\Glink.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[-1]
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Glink.csproj must declare a three-component release Version.'
}
$buildRoot = Join-Path $PSScriptRoot '.build'
$publishDirectory = Join-Path $buildRoot 'publish'
$outputDirectory = if ($SmokeTest) { Join-Path $buildRoot 'smoke' } else { Join-Path $root 'artifacts' }
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

if (!$SkipPublish) {
    if (Test-Path -LiteralPath $publishDirectory) {
        $resolvedPublish = (Resolve-Path -LiteralPath $publishDirectory).Path
        $allowedParent = [IO.Path]::GetFullPath($buildRoot) + [IO.Path]::DirectorySeparatorChar
        if (!$resolvedPublish.StartsWith($allowedParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to clean a directory outside packaging/.build.'
        }
        Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
    }
    # 结尾的反斜杠会在 PowerShell 5.1 里转义参数引号，把后续参数粘进 -o 的值。
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false -p:CanvasReview=false -o $publishDirectory --no-restore -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }

    if (!$VcRuntimeDirectory) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (!(Test-Path -LiteralPath $vswhere)) {
            throw 'Install the Visual Studio C++ build tools, or pass -VcRuntimeDirectory with its official x64 Microsoft.VC143.CRT redistributable directory.'
        }
        $visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        $redistRoot = Join-Path $visualStudio 'VC\Redist\MSVC'
        $VcRuntimeDirectory = Get-ChildItem -LiteralPath $redistRoot -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (!$VcRuntimeDirectory -or !(Test-Path -LiteralPath (Join-Path $VcRuntimeDirectory 'vcruntime140.dll'))) {
        throw 'The official x64 Visual C++ runtime directory was not found.'
    }
    foreach ($runtime in Get-ChildItem -LiteralPath $VcRuntimeDirectory -Filter '*.dll' -File) {
        if ((Get-AuthenticodeSignature -LiteralPath $runtime.FullName).Status -ne 'Valid') {
            throw "Invalid Visual C++ runtime publisher signature: $($runtime.Name)"
        }
        Copy-Item -LiteralPath $runtime.FullName -Destination $publishDirectory
    }

    # Preserve the redistribution notices shipped with restored dependencies.
    $assetsPath = Join-Path $root 'Glink\obj\project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        foreach ($packageRoot in $packageRoots) {
            $packageDirectory = Join-Path $packageRoot $library.Value.path
            if (!(Test-Path -LiteralPath $packageDirectory)) { continue }
            $notices = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '^(license|notice|third.party.notices)(\.|$)' })
            if ($notices.Count -eq 0) { continue }
            $noticeDirectory = Join-Path $publishDirectory ('licenses\' + $library.Name.Replace('/', '-'))
            New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
            $notices | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $noticeDirectory }
            break
        }
    }
}

$requiredFiles = @('Glink.exe', 'Glink.dll', 'Glink.pri', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'e_sqlite3.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')
foreach ($file in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $file))) { throw "Self-contained payload is missing $file." }
}
$executable = Get-Item -LiteralPath (Join-Path $publishDirectory 'Glink.exe')
if ($executable.VersionInfo.ProductVersion -notmatch ('^' + [regex]::Escape($version) + '(\+|$)')) {
    throw 'Published executable version does not match the project release version.'
}
$runtimeConfiguration = Get-Content -LiteralPath (Join-Path $publishDirectory 'Glink.runtimeconfig.json') -Raw | ConvertFrom-Json
if (!$runtimeConfiguration.runtimeOptions.includedFrameworks) { throw 'The payload is not .NET self-contained.' }
if (Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object { $_.Name -match '\.(pdb|db|db-shm|db-wal)$' }) {
    throw 'Release payload unexpectedly contains debugging symbols or user databases.'
}

& (Join-Path $PSScriptRoot 'New-MsiPackage.ps1') -PublishDirectory $publishDirectory -OutputDirectory $outputDirectory -Version $version -SmokeTest:$SmokeTest | Out-Null
$suffix = if ($SmokeTest) { 'Installer-Test' } else { 'Setup' }
$installer = Join-Path $outputDirectory "Glink-$version-$suffix-x64.msi"
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding utf8
Write-Output $installer

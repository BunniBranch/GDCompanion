[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [ValidatePattern('^(-[a-zA-Z0-9]+)?$')][string]$DestinationSuffix = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$source = Join-Path $root 'src'
$destination = Join-Path $root "build\GDCompanion-2.3.91$DestinationSuffix"
$nativeBuild = Join-Path $root "build_native$DestinationSuffix"
$dotnet = Join-Path $root '.tools\dotnet-sdk\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet).Source }
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ build tools were not found.' }
$devShell = Join-Path $vs 'Common7\Tools\Launch-VsDevShell.ps1'
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:APPDATA = Join-Path $root '.dotnet-home\AppData\Roaming'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& $devShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
& $cmake -S (Join-Path $source 'GrimDawnBridge') -B $nativeBuild -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" "-DCMAKE_BUILD_TYPE=$Configuration"
if ($LASTEXITCODE -ne 0) { throw 'Native bridge configuration failed.' }
& $cmake --build $nativeBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed.' }
if (-not $SkipTests) {
    & (Join-Path $nativeBuild 'ItemAffixTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native item affix tests failed.' }
    & (Join-Path $nativeBuild 'CharacterResourceTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native character resource tests failed.' }
    & (Join-Path $nativeBuild 'CharacterLevelTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native character level tests failed.' }
    & (Join-Path $nativeBuild 'RadarCanvasStateTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native radar canvas tests failed.' }
    & (Join-Path $nativeBuild 'QuestStageCaptureTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native quest-stage capture tests failed.' }
    & (Join-Path $nativeBuild 'PersistentBookmarkTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native persistent bookmark tests failed.' }
    & (Join-Path $nativeBuild 'MasteryRespecTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native mastery transaction tests failed.' }
    & (Join-Path $nativeBuild 'GameplayAssistTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native gameplay assist tests failed.' }
    & (Join-Path $nativeBuild 'GameplayAssistRuntimeTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native gameplay assist runtime tests failed.' }
}

if (Test-Path -LiteralPath $destination) {
    # Do not partially clean an app directory currently used by the user.
    $runningCompanions = Get-Process GDCompanion,GrimDawnCompanion -ErrorAction SilentlyContinue
    foreach ($runningCompanion in $runningCompanions) {
        if ($runningCompanion.Path -and [IO.Path]::GetDirectoryName($runningCompanion.Path) -eq $destination) {
            throw 'This build folder is running. Close it first or use -DestinationSuffix to build separately.'
        }
    }
    $resolvedRoot = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedDestination = (Resolve-Path -LiteralPath $destination).Path
    if (-not $resolvedDestination.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to clean a build path outside the workspace.' }
    Remove-Item -LiteralPath $resolvedDestination -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null

& $dotnet restore (Join-Path $source 'GrimDawnCompanion.App\GrimDawnCompanion.App.csproj') -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Desktop application restore failed.' }
& $dotnet publish (Join-Path $source 'GrimDawnCompanion.App\GrimDawnCompanion.App.csproj') -c $Configuration -p:Platform=x64 --no-restore -o $destination --self-contained false
if ($LASTEXITCODE -ne 0) { throw 'Desktop application publish failed.' }
Copy-Item -LiteralPath (Join-Path $nativeBuild 'GrimDawnBridge.dll') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $source 'ThirdParty\minhook\LICENSE.txt') -Destination (Join-Path $destination 'MINHOOK-LICENSE.txt') -Force
$releaseDocs = Join-Path $destination 'docs'
New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
foreach ($doc in 'user-guide.md','developer-guide.md') {
    Copy-Item -LiteralPath (Join-Path $root "docs\$doc") -Destination $releaseDocs
}

if (-not $SkipTests) {
    & $dotnet restore (Join-Path $source 'GrimDawnCompanion.Tests\GrimDawnCompanion.Tests.csproj') -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
    & $dotnet build (Join-Path $source 'GrimDawnCompanion.Tests\GrimDawnCompanion.Tests.csproj') -c $Configuration -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $dotnet (Join-Path $source "GrimDawnCompanion.Tests\bin\x64\$Configuration\net8.0-windows\GrimDawnCompanion.Tests.dll") $root
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

# Fail closed if generated catalogs, extracted archives or saves enter a release.
$forbidden = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {
    $_.Name -eq 'catalog.json' -or $_.Extension -in '.arz', '.arc', '.dbr', '.gdc', '.gst', '.gsh', '.pdb' -or
    $_.FullName.StartsWith((Join-Path $destination 'Data') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
})
if ($forbidden.Count -gt 0) { throw 'Release contains generated game data or saves. Packaging stopped.' }
foreach ($binary in (Get-ChildItem -LiteralPath $destination -File | Where-Object { $_.Extension -in '.exe', '.dll' })) {
    $binaryBytes = [IO.File]::ReadAllBytes($binary.FullName)
    foreach ($binaryText in @([Text.Encoding]::UTF8.GetString($binaryBytes), [Text.Encoding]::Unicode.GetString($binaryBytes))) {
        if ($binaryText.Contains($root) -or $binaryText -match '(?i)[A-Z]:[\\/]+Users[\\/]') {
            throw "Local user build path found in $($binary.Name). Packaging stopped."
        }
    }
}
foreach ($notice in 'LICENSE','COPYRIGHT.md','THIRD-PARTY-NOTICES.md','MINHOOK-LICENSE.txt','Assets\Fonts\OFL.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $destination $notice))) { throw "Missing release notice: $notice" }
}

$sourceZip = Join-Path $root 'build\GDCompanion-v2.3.91-source.zip'
& (Join-Path $PSScriptRoot 'package-source.ps1') -DestinationZip $sourceZip

$zip = Join-Path $root 'build\GDCompanion-v2.3.91-win-x64.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Build ready: $destination"
Write-Host "Zip ready:   $zip"
Write-Host "Source ready: $sourceZip"

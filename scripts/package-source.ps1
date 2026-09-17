[CmdletBinding()]
param([Parameter(Mandatory)][string]$DestinationZip)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$outputPath = [IO.Path]::GetFullPath($DestinationZip)
$buildPrefix = (Join-Path $root 'build') + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($buildPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($outputPath) -ne '.zip' -or
    [IO.Path]::GetDirectoryName($outputPath) -ne (Join-Path $root 'build')) {
    throw 'Source archives must be ZIP files directly in the workspace build directory.'
}
$manifest = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'source-manifest.txt') |
    Where-Object { $_.Trim() -and -not $_.StartsWith('#') })
if ($manifest.Count -eq 0 -or @($manifest | Sort-Object -Unique).Count -ne $manifest.Count) {
    throw 'The source manifest is empty or contains duplicates.'
}
$files = foreach ($relative in $manifest) {
    if ($relative -match '^docs[\\/]' -and $relative -notin 'docs/user-guide.md','docs/developer-guide.md') {
        throw "Only current public guides belong in source releases: $relative"
    }
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or
        $relative -match '(^|/)(bin|obj|Data|build|tmp|research|catalog_work)/|\.(arz|arc|dbr|gdc|gst|gsh|pdb|exe|dll|pfx|p12|key|zip)$') {
        throw "Disallowed source entry: $relative"
    }
    $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing or invalid source entry: $relative" }
    # Do not follow source-file links into unrelated/private directories.
    for ($walk = $full; $walk -ne $root; $walk = [IO.Path]::GetDirectoryName($walk)) {
        $ancestor = Get-Item -LiteralPath $walk
        if ($ancestor.LinkType -in 'SymbolicLink', 'Junction') {
            throw "Linked source entry needs manual review: $relative"
        }
    }
    [pscustomobject]@{ Relative = $relative; Full = $full }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Force }
$archive = [IO.Compression.ZipFile]::Open($outputPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.Full,
            $file.Relative.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Write-Host "Source archive: $($files.Count) explicitly reviewed files"

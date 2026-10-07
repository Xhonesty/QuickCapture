param([string]$SourceDirectory, [string]$OutputFile)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dist = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory, $projectRoot) } else { Join-Path $projectRoot 'dist' }
if (-not $dist.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package source must stay inside the QuickCapture project.' }
$stagingParent = Join-Path $projectRoot 'artifacts\staging'
# A previous portable copy may have been launched and contain settings/captures.
# Always stage in a new directory so those files cannot enter the next archive.
$stagingRoot = Join-Path $stagingParent ('package-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $stagingRoot 'QuickCapture'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
foreach ($entry in Get-ChildItem -LiteralPath $dist) {
    if ($entry.Name -notin @('Data','Captures','Diagnostics')) {
        Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse -Force
    }
}
$archive = if ($OutputFile) { [IO.Path]::GetFullPath($OutputFile, $projectRoot) } else { Join-Path $projectRoot 'artifacts\QuickCapture-win-x64.zip' }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if (-not $archive.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($archive) -ne '.zip') { throw 'Package output must be a ZIP inside QuickCapture/artifacts.' }
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($archive)) -Force | Out-Null
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$resolvedStage = (Resolve-Path -LiteralPath $stagingRoot).Path
$resolvedParent = (Resolve-Path -LiteralPath $stagingParent).Path
if ([System.IO.Path]::GetDirectoryName($resolvedStage) -ne $resolvedParent) { throw 'Unexpected staging path; refusing cleanup.' }
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
Write-Output $archive

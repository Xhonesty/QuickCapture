$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $projectRoot 'dist'
$stagingRoot = Join-Path $projectRoot 'artifacts\staging'
$stage = Join-Path $stagingRoot 'QuickCapture'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
foreach ($entry in Get-ChildItem -LiteralPath $dist) {
    if ($entry.Name -notin @('Data','Captures','Diagnostics')) {
        Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse -Force
    }
}
$archive = Join-Path $projectRoot 'artifacts\QuickCapture-win-x64.zip'
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stagingRoot, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
Write-Output $archive

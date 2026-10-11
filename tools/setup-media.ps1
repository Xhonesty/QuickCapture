param([string]$ArchivePath, [switch]$Offline, [string]$Python = 'python', [string]$Bash)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $projectRoot 'vendor/media'
if (-not $ArchivePath) {
    & (Join-Path $PSScriptRoot 'build-media.ps1') -Offline:$Offline -Python $Python -Bash $Bash
    return
}
$manifest = Get-Content -LiteralPath (Join-Path $destination 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.layout -ne 'shared' -or (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $manifest.archiveSha256) { throw 'Shared media bundle SHA-256 does not match the pinned manifest.' }
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    foreach ($file in $manifest.files) {
        if ($file.name -ne [IO.Path]::GetFileName($file.name) -or $file.name -notmatch '\.(exe|dll)$') { throw 'Invalid media manifest filename.' }
        $entry = $zip.GetEntry($file.name)
        if (-not $entry -or $entry.Length -ne $file.size) { throw "Media dependency missing: $($file.name)" }
        $target = [IO.Path]::GetFullPath((Join-Path $destination $file.name))
        if ([IO.Path]::GetDirectoryName($target) -ne [IO.Path]::GetFullPath($destination)) { throw 'Unsafe media output path.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw "Media dependency checksum mismatch: $($file.name)" }
    }
} finally { $zip.Dispose() }
Write-Output "Verified shared FFmpeg $($manifest.version) and all required DLLs."

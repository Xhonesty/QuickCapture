param([string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $projectRoot 'vendor\media'
$manifest = Get-Content -LiteralPath (Join-Path $destination 'manifest.json') -Raw | ConvertFrom-Json
$downloadDirectory = Join-Path $projectRoot '.tools\media-download'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
if (!$ArchivePath) {
    $ArchivePath = Join-Path $downloadDirectory 'ffmpeg-essentials.zip'
    if (!(Test-Path -LiteralPath $ArchivePath)) { Invoke-WebRequest -Uri $manifest.url -OutFile $ArchivePath }
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $manifest.archiveSha256) { throw 'FFmpeg ZIP 校验失败；请检查固定版本下载，不会安装不匹配的文件。' }
# Extract only the two expected executables, with no recursive moving or deleting.
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    foreach ($name in @('ffmpeg', 'ffprobe')) {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq "ffmpeg-9.0.2-essentials_build/bin/$name.exe" }
        if (!$entry) { throw "缺少 $name.exe" }
        $target = Join-Path $destination "$name.exe"
        $resolvedTarget = [System.IO.Path]::GetFullPath($target)
        if (![System.IO.Path]::GetDirectoryName($resolvedTarget).Equals([System.IO.Path]::GetFullPath($destination), [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe output path.' }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $resolvedTarget, $true)
        if ((Get-FileHash -LiteralPath $resolvedTarget -Algorithm SHA256).Hash -ne $manifest."${name}Sha256") { throw "$name.exe 校验失败" }
    }
} finally { $zip.Dispose() }
Write-Output "已安装固定版本 FFmpeg $($manifest.version)，运行 tools/build.ps1 将工具加入便携版。"

param([switch]$Offline, [switch]$SkipBuild, [switch]$PayloadOnly, [string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$dist = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory, $projectRoot) } elseif ($SkipBuild) { Join-Path $projectRoot 'dist' } else { Join-Path $artifactRoot ('builds\release-' + [guid]::NewGuid().ToString('N')) }
if (-not $dist.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release source must stay inside the QuickCapture project.' }
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
. (Join-Path $PSScriptRoot 'ReleasePayload.ps1')
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\QuickCapture.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Offline:$Offline -OutputDirectory $dist }
$payloadManifest = Assert-QuickCaptureReleasePayload $dist
if ((Get-Item -LiteralPath (Join-Path $dist 'QuickCapture.exe')).VersionInfo.ProductVersion.Split('+')[0] -ne $version) { throw 'Build and source versions differ.' }
$readmeHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'README.md') -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-FileHash -LiteralPath (Join-Path $dist 'README.md') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $readmeHash) { throw 'Build README differs from the project README.' }
if (Test-Path -LiteralPath (Join-Path $dist 'docs\local')) { throw 'Remove local maintenance documents from dist before packaging.' }
function Backup-Package([string]$Path, [string]$PackageVersion) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $PackageVersion = $PackageVersion.Trim()
    if ($PackageVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Cannot determine existing package version: $Path" }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $folder = Assert-QuickCaptureArtifactPath (Join-Path $artifactRoot "backups\v$PackageVersion") $artifactRoot
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $name = [IO.Path]::GetFileNameWithoutExtension($Path) + '-' + $hash.Substring(0,12) + [IO.Path]::GetExtension($Path)
    $backup = Assert-QuickCaptureArtifactPath (Join-Path $folder $name) $artifactRoot
    if (-not (Test-Path -LiteralPath $backup)) { Copy-Item -LiteralPath $Path -Destination $backup }
    if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "Backup checksum mismatch: $backup" }
    [IO.File]::WriteAllText($backup + '.sha256', "$hash  $name`n", [Text.Encoding]::ASCII)
    return $backup
}
$portable = Join-Path $artifactRoot 'QuickCapture-win-x64.zip'
if (Test-Path -LiteralPath $portable) { $null = Backup-Package $portable (Get-QuickCaptureArchiveVersion $portable) }
$installer = Join-Path $artifactRoot "QuickCapture-Setup-$version-x64.exe"
$previousInstaller = if (Test-Path -LiteralPath $installer) { Backup-Package $installer ((Get-Item -LiteralPath $installer).VersionInfo.ProductVersion.Split('+')[0]) } else { $null }
if (-not $previousInstaller) {
    $previousCandidate = Get-ChildItem -LiteralPath $artifactRoot -Filter 'QuickCapture-Setup-*-x64.exe' -File | Where-Object { $_.VersionInfo.ProductVersion.Split('+')[0].Trim() -match '^\d+\.\d+\.\d+$' -and [version]$_.VersionInfo.ProductVersion.Split('+')[0].Trim() -lt [version]$version } | Sort-Object { [version]$_.VersionInfo.ProductVersion.Split('+')[0].Trim() } -Descending | Select-Object -First 1
    if ($previousCandidate) { $previousInstaller = Backup-Package $previousCandidate.FullName $previousCandidate.VersionInfo.ProductVersion.Split('+')[0] }
}
& (Join-Path $PSScriptRoot 'package.ps1') -SourceDirectory $dist -OutputFile $portable
& (Join-Path $PSScriptRoot 'build-installer.ps1') -SkipBuild -SourceDirectory $dist
$dllHash = (Get-FileHash -LiteralPath (Join-Path $dist 'QuickCapture.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-QuickCaptureArchiveVersion $portable) -ne $version) { throw 'Portable ZIP version differs.' }
$zip = [IO.Compression.ZipFile]::OpenRead($portable)
try {
    if ($zip.Entries | Where-Object { $_.FullName -match '^QuickCapture/(Data|Captures|Diagnostics)/' }) { throw 'Portable ZIP contains personal/test data.' }
    if ($zip.Entries | Where-Object { $_.FullName -match '^QuickCapture/docs/local(/|$)' }) { throw 'Portable ZIP contains local maintenance documents.' }
    $readmeStream = $zip.GetEntry('QuickCapture/README.md').Open()
    try { $zipReadmeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($readmeStream)).ToLowerInvariant() } finally { $readmeStream.Dispose() }
    if ($zipReadmeHash -ne $readmeHash) { throw 'Portable ZIP README differs from the project README.' }
    $stream = $zip.GetEntry('QuickCapture/QuickCapture.dll').Open()
    try { $zipDllHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
    if ($zipDllHash -ne $dllHash) { throw 'Portable ZIP and dist DLL differ.' }
} finally { $zip.Dispose() }
$portableHash = (Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($portable + '.sha256', "$portableHash  $([IO.Path]::GetFileName($portable))`n", [Text.Encoding]::ASCII)
if ($PayloadOnly) { & (Join-Path $PSScriptRoot 'verify-release-payload.ps1') -InstallerFile $installer -SourceDirectory $dist }
else { & (Join-Path $PSScriptRoot 'verify-installer.ps1') -InstallerFile $installer -PreviousInstallerFile $previousInstaller -SourceDirectory $dist }
$checks = Get-Content -LiteralPath (Join-Path $artifactRoot 'installer-verification.json') -Raw | ConvertFrom-Json
if (@($checks.checks | Where-Object { -not $_.passed }).Count -gt 0) { throw 'Installer verification failed.' }
$manifest = [ordered]@{version=$version;builtAt=(Get-Date -Format o);sourceCommit=$payloadManifest.sourceCommit;applicationDirectory=$dist;applicationDllSha256=$dllHash;portable=[ordered]@{path=$portable;sha256=$portableHash};installer=[ordered]@{path=$installer;sha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()};installerChecksPassed=@($checks.checks).Count;installerVerificationMode=$(if($PayloadOnly){'payload-only'}else{'full'});previousInstallerBackup=$previousInstaller}
[IO.File]::WriteAllText((Join-Path $artifactRoot 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
# Cleanup runs only after both packages and the installed DLL are verified.
& (Join-Path $PSScriptRoot 'prune-backups.ps1')
Write-Output (Join-Path $artifactRoot 'release-manifest.json')

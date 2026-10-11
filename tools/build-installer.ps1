param([switch]$Offline, [switch]$SkipBuild, [string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot 'artifacts'
$source = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory, $projectRoot) } else { Join-Path $artifactRoot ('builds\installer-' + [guid]::NewGuid().ToString('N')) }
if (-not $source.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer source must stay inside QuickCapture.' }
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Offline:$Offline -OutputDirectory $source }
. (Join-Path $PSScriptRoot 'ReleasePayload.ps1')
$null = Assert-QuickCaptureReleasePayload $source
$executable = Join-Path $source 'QuickCapture.exe'
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\QuickCapture.csproj')
$version = $project.Project.PropertyGroup.Version
if (-not (Test-Path -LiteralPath $executable) -or (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion.Split('+')[0] -ne $version) { throw 'Build and source versions do not match.' }
foreach ($required in @('QuickCapture.runtimeconfig.json','Tools\media\ffmpeg.exe','Tools\media\ffprobe.exe','Tools\ocr\tessdata\eng.traineddata','Tools\ocr\tessdata\chi_sim.traineddata','Tools\ocr\tessdata\chi_tra.traineddata','QuickCapture-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "Installer payload missing: $required" }
}
$compiler = & (Join-Path $PSScriptRoot 'setup-installer.ps1')
$uninstallLauncher = & (Join-Path $PSScriptRoot 'build-uninstall-launcher.ps1')
$stagingParent = Join-Path $artifactRoot 'staging'
$stage = Join-Path $stagingParent ('installer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    Copy-QuickCaptureReleasePayload $source $stage
    & $compiler '--quiet' ("--define=PayloadDir=$stage") ("--define=ArtifactDir=$artifactRoot") ("--define=AppVersion=$version") ("--define=UninstallLauncher=$uninstallLauncher") (Join-Path $projectRoot 'installer\QuickCapture.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $output = Join-Path $artifactRoot "QuickCapture-Setup-$version-x64.exe"
    $hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($output + '.sha256', "$hash  $([IO.Path]::GetFileName($output))`n", [Text.Encoding]::ASCII)
    Write-Output $output
    Write-Output "SHA256: $hash"
} finally {
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $resolvedParent = (Resolve-Path -LiteralPath $stagingParent).Path
    if ([IO.Path]::GetDirectoryName($resolvedStage) -ne $resolvedParent) { throw 'Unexpected installer staging path; refusing cleanup.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}

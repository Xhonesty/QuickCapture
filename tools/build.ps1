param([switch]$Offline, [switch]$Test, [switch]$Microphone, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory, $projectRoot) } else { Join-Path $projectRoot ('artifacts\builds\application-' + [guid]::NewGuid().ToString('N')) }
if (-not $publishOutput.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay inside the QuickCapture project.' }
# A fresh directory prevents stale runtime files, diagnostics and user media
# from entering a new release; an earlier dist copy is never deleted.
if ((Test-Path -LiteralPath $publishOutput) -and @(Get-ChildItem -LiteralPath $publishOutput -Force).Count -gt 0) { throw 'Publish directory must be new or empty; existing files and user data are preserved.' }
$cursor = $publishOutput
while ($cursor -and $cursor.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Publish path cannot contain reparse points.' }
    $cursor = [IO.Path]::GetDirectoryName($cursor)
}
$localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot '.tools\http-cache'
$env:TEMP = Join-Path $projectRoot '.tools\temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
$publishArgs = @('publish', (Join-Path $projectRoot 'src\QuickCapture.csproj'), '-c', 'Release', '-p:Platform=x64', '-r', 'win-x64', '--self-contained', 'true', '-o', $publishOutput, '-p:DebugType=None', '-p:DebugSymbols=false')
if ($Offline) { $publishArgs += @('--source', (Join-Path $projectRoot '.tools\feed'), '-p:NuGetAudit=false') }
& $sdk @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# Native NuGet dependencies copy large debug symbols even with DebugSymbols=false.
# These files are build outputs, unnecessary in the portable runtime.
foreach ($symbolFile in Get-ChildItem -LiteralPath $publishOutput -Filter '*.pdb' -File) { Remove-Item -LiteralPath $symbolFile.FullName }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishOutput
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $publishOutput 'QuickCapture-NOTICES.txt')
$mediaSource = Join-Path $projectRoot 'vendor\media'
$mediaManifest = Get-Content -LiteralPath (Join-Path $mediaSource 'manifest.json') -Raw | ConvertFrom-Json
if ($mediaManifest.layout -ne 'shared' -or -not $mediaManifest.files) { throw 'Shared FFmpeg runtime is not prepared. Run tools/build-media.ps1 or tools/setup-media.ps1 -ArchivePath.' }
$mediaDestination = Join-Path $publishOutput 'Tools\media'
New-Item -ItemType Directory -Path $mediaDestination -Force | Out-Null
foreach ($file in $mediaManifest.files) {
    if ($file.name -ne [IO.Path]::GetFileName($file.name) -or $file.name -notmatch '\.(exe|dll)$') { throw 'Invalid media manifest filename.' }
    $path = Join-Path $mediaSource $file.name
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Shared media dependency missing or changed: $($file.name)" }
    Copy-Item -LiteralPath $path -Destination $mediaDestination
}
Copy-Item -LiteralPath (Join-Path $mediaSource 'manifest.json'),(Join-Path $mediaSource 'build-inputs.json') -Destination $mediaDestination
$licenseDestination = Join-Path $publishOutput 'licenses'
$ocrManifestPath = Join-Path $projectRoot 'vendor\ocr\manifest.json'
$ocrManifest = Get-Content -LiteralPath $ocrManifestPath -Raw | ConvertFrom-Json
$ocrDestination = Join-Path $publishOutput 'Tools\ocr\tessdata'
New-Item -ItemType Directory -Path $ocrDestination -Force | Out-Null
foreach ($model in $ocrManifest.models) {
    $modelPath = Join-Path $projectRoot ('vendor\ocr\tessdata\' + $model.name)
    if (-not (Test-Path -LiteralPath $modelPath) -or (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash -ne $model.sha256) { throw 'OCR 模型缺失或校验失败，请运行 python tools/setup-ocr.py。' }
    Copy-Item -LiteralPath $modelPath -Destination $ocrDestination -Force
}
Copy-Item -LiteralPath $ocrManifestPath -Destination (Join-Path $publishOutput 'Tools\ocr\manifest.json') -Force
# QuickCapture targets x64; omit the wrapper's redundant x86 native binaries.
$unusedOcrNative = Join-Path $publishOutput 'x86'
if (Test-Path -LiteralPath $unusedOcrNative) {
    $resolvedOcrNative = (Resolve-Path -LiteralPath $unusedOcrNative).Path
    if ([IO.Path]::GetDirectoryName($resolvedOcrNative) -ne $publishOutput.TrimEnd('\')) { throw 'Unexpected OCR native output path.' }
    Remove-Item -LiteralPath $resolvedOcrNative -Recurse -Force
}
New-Item -ItemType Directory -Path $licenseDestination -Force | Out-Null
foreach ($licenseFile in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs\licenses')) { Copy-Item -LiteralPath $licenseFile.FullName -Destination $licenseDestination -Recurse -Force }
$upgradeDocs = Join-Path $publishOutput 'docs'
New-Item -ItemType Directory -Path $upgradeDocs -Force | Out-Null
# User-facing install help and licenses are shipped; development records and
# historical preview images remain in the source tree.
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\installer.md') -Destination $upgradeDocs
$readmeImages = Join-Path $upgradeDocs 'images'
New-Item -ItemType Directory -Path $readmeImages -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\images\main-simplified-light.png') -Destination $readmeImages
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishOutput 'QuickCapture.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    if ($framework.name -eq 'Microsoft.NETCore.App') { $label = 'Core'; $packageName = 'microsoft.netcore.app.runtime.win-x64' }
    elseif ($framework.name -eq 'Microsoft.WindowsDesktop.App') { $label = 'Desktop'; $packageName = 'microsoft.windowsdesktop.app.runtime.win-x64' }
    else { continue }
    $packageRoot = Join-Path $projectRoot ".tools\packages\$packageName\$($framework.version)"
    $licenseName = if ($label -eq 'Desktop') { 'LICENSE' } else { 'LICENSE.TXT' }
    Copy-Item -LiteralPath (Join-Path $packageRoot $licenseName) -Destination (Join-Path $publishOutput "DotNet-$label-LICENSE.txt")
    if ($label -eq 'Core') { Copy-Item -LiteralPath (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $publishOutput 'DotNet-Core-NOTICES.txt') }
}
$files = @(Get-ChildItem -LiteralPath $publishOutput -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($publishOutput, $_.FullName).Replace('\','/');size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest = [ordered]@{schema=1;version=(Get-Item -LiteralPath (Join-Path $publishOutput 'QuickCapture.exe')).VersionInfo.ProductVersion.Split('+')[0];sourceCommit=(& git -C $projectRoot rev-parse HEAD).Trim();files=$files}
[IO.File]::WriteAllText((Join-Path $publishOutput 'payload-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
. (Join-Path $PSScriptRoot 'ReleasePayload.ps1')
$null = Assert-QuickCaptureReleasePayload $publishOutput
if ($Test) {
    $testArgs = @('--self-test','--audio')
    if ($Microphone) { $testArgs += '--microphone' }
    & (Join-Path $PSScriptRoot 'test.ps1') -Offline:$Offline -SourceDirectory $publishOutput -Arguments $testArgs
}
Write-Output (Join-Path $publishOutput 'QuickCapture.exe')

param([switch]$Offline, [switch]$Test, [switch]$Microphone, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory, $projectRoot) } else { Join-Path $projectRoot 'dist' }
if (-not $publishOutput.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output must stay inside the QuickCapture project.' }
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
if (Test-Path -LiteralPath (Join-Path $projectRoot 'docs\verification.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\verification.md') -Destination (Join-Path $publishOutput 'verification.md') }
if (Test-Path -LiteralPath (Join-Path $projectRoot 'docs\icon-processing.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\icon-processing.md') -Destination (Join-Path $publishOutput 'icon-processing.md') }
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $publishOutput 'QuickCapture-NOTICES.txt')
$mediaSource = Join-Path $projectRoot 'vendor\media'
if ((Test-Path -LiteralPath (Join-Path $mediaSource 'ffmpeg.exe')) -and (Test-Path -LiteralPath (Join-Path $mediaSource 'ffprobe.exe'))) {
    $mediaManifest = Get-Content -LiteralPath (Join-Path $mediaSource 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($mediaName in @('ffmpeg', 'ffprobe')) { if ((Get-FileHash -LiteralPath (Join-Path $mediaSource "$mediaName.exe") -Algorithm SHA256).Hash -ne $mediaManifest."${mediaName}Sha256") { throw "$mediaName.exe 与固定版本清单不符，请运行 tools/setup-media.ps1。" } }
    $mediaDestination = Join-Path $publishOutput 'Tools\media'
    New-Item -ItemType Directory -Path $mediaDestination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $mediaSource 'ffmpeg.exe'), (Join-Path $mediaSource 'ffprobe.exe') -Destination $mediaDestination
    if (Test-Path -LiteralPath (Join-Path $mediaSource 'manifest.json')) { Copy-Item -LiteralPath (Join-Path $mediaSource 'manifest.json') -Destination $mediaDestination }
} else { Write-Warning 'FFmpeg 未捆绑：运行 tools/setup-media.ps1 后重新构建；当前仍可录制并保存原生 MP4。' }
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
foreach ($licenseFile in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs\licenses') -File) { Copy-Item -LiteralPath $licenseFile.FullName -Destination $licenseDestination -Force }
$upgradeDocs = Join-Path $publishOutput 'docs'
New-Item -ItemType Directory -Path $upgradeDocs -Force | Out-Null
foreach ($document in @('recording-editor-and-context-toolbar.md', 'toolbar-and-previews.md', 'office-tools.md', 'ocr-options.md', 'roadmap.md', 'feature-upgrade.md', 'in-place-text.md', 'panel-optimization.md', 'recording-preferences.md', 'design-spec.md', 'media-tools.md', 'verification.md', 'icon-processing.md')) {
    $documentationSource = Join-Path $projectRoot "docs\$document"
    if (Test-Path -LiteralPath $documentationSource) { Copy-Item -LiteralPath $documentationSource -Destination $upgradeDocs -Force }
}
$documentationImages = Join-Path $projectRoot 'docs\images'
if (Test-Path -LiteralPath $documentationImages) { Copy-Item -LiteralPath $documentationImages -Destination $upgradeDocs -Recurse -Force }
# Keep the earlier root-level copies for portable-package compatibility.
foreach ($document in @('design-spec.md', 'media-tools.md')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot "docs\$document")) { Copy-Item -LiteralPath (Join-Path $projectRoot "docs\$document") -Destination $publishOutput }
}
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
if ($Test) {
    $testArgs = @('--self-test','--audio')
    if ($Microphone) { $testArgs += '--microphone' }
    $process = Start-Process -FilePath (Join-Path $publishOutput 'QuickCapture.exe') -ArgumentList $testArgs -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw 'Integration tests failed. Read dist\Diagnostics\results.json.' }
}
Write-Output (Join-Path $publishOutput 'QuickCapture.exe')

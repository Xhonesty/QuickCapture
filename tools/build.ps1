param([switch]$Offline, [switch]$Test, [switch]$Microphone)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$publishArgs = @('publish', (Join-Path $projectRoot 'src\QuickCapture.csproj'), '-c', 'Release', '-p:Platform=x64', '-r', 'win-x64', '--self-contained', 'true', '-o', (Join-Path $projectRoot 'dist'), '-p:DebugType=None', '-p:DebugSymbols=false')
if ($Offline) { $publishArgs += @('--source', (Join-Path $projectRoot '.tools\feed'), '-p:NuGetAudit=false') }
& $sdk @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $projectRoot 'dist')
if (Test-Path -LiteralPath (Join-Path $projectRoot 'docs\verification.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\verification.md') -Destination (Join-Path $projectRoot 'dist\verification.md') }
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $projectRoot 'dist\QuickCapture-NOTICES.txt')
$runtimeConfig = Get-Content -LiteralPath (Join-Path $projectRoot 'dist\QuickCapture.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    if ($framework.name -eq 'Microsoft.NETCore.App') { $label = 'Core'; $packageName = 'microsoft.netcore.app.runtime.win-x64' }
    elseif ($framework.name -eq 'Microsoft.WindowsDesktop.App') { $label = 'Desktop'; $packageName = 'microsoft.windowsdesktop.app.runtime.win-x64' }
    else { continue }
    $packageRoot = Join-Path $projectRoot ".tools\packages\$packageName\$($framework.version)"
    $licenseName = if ($label -eq 'Desktop') { 'LICENSE' } else { 'LICENSE.TXT' }
    Copy-Item -LiteralPath (Join-Path $packageRoot $licenseName) -Destination (Join-Path $projectRoot "dist\DotNet-$label-LICENSE.txt")
    if ($label -eq 'Core') { Copy-Item -LiteralPath (Join-Path $packageRoot 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $projectRoot 'dist\DotNet-Core-NOTICES.txt') }
}
if ($Test) {
    $testArgs = @('--self-test','--audio')
    if ($Microphone) { $testArgs += '--microphone' }
    $process = Start-Process -FilePath (Join-Path $projectRoot 'dist\QuickCapture.exe') -ArgumentList $testArgs -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw 'Integration tests failed. Read dist\Diagnostics\results.json.' }
}
Write-Output (Join-Path $projectRoot 'dist\QuickCapture.exe')

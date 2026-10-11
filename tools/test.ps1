param([switch]$Offline, [Parameter(Mandatory)][string]$SourceDirectory, [string[]]$Arguments = @('--self-test','--architecture-only'))
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = [IO.Path]::GetFullPath($SourceDirectory, $projectRoot)
if (-not $source.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Test source must stay inside QuickCapture.' }
. (Join-Path $PSScriptRoot 'ReleasePayload.ps1')
$null = Assert-QuickCaptureReleasePayload $source
$sdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli-home'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot '.tools/http-cache'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TEMP = Join-Path $projectRoot '.tools/temp'; $env:TMP = $env:TEMP
$testRoot = Join-Path $projectRoot ('artifacts/test-runs/' + [guid]::NewGuid().ToString('N'))
$publishArgs = @('publish', (Join-Path $projectRoot 'tests/QuickCapture.Tests/QuickCapture.Tests.csproj'), '-c','Release','-p:Platform=x64','-r','win-x64','--self-contained','true','-o',$testRoot,'-p:BuildProjectReferences=false','-p:DebugType=None','-p:DebugSymbols=false')
if ($Offline) { $publishArgs += @('--source',(Join-Path $projectRoot '.tools/feed'),'-p:NuGetAudit=false') }
& $sdk @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Test runner compilation failed.' }
# Use the already-published app/runtime, not another production build.
Copy-QuickCaptureReleasePayload $source $testRoot
Copy-Item -LiteralPath (Join-Path $source 'QuickCapture.runtimeconfig.json') -Destination (Join-Path $testRoot 'QuickCapture.Tests.runtimeconfig.json') -Force
if (Test-Path -LiteralPath (Join-Path $source 'installed-mode')) { Copy-Item -LiteralPath (Join-Path $source 'installed-mode') -Destination $testRoot }
if ($Arguments -contains '--failed-only') {
    $previous = Join-Path $source 'Diagnostics'
    if (-not (Test-Path -LiteralPath (Join-Path $previous 'results.json'))) { throw 'Failed-only checks require the previous diagnostic results in the source directory.' }
    Copy-Item -LiteralPath $previous -Destination $testRoot -Recurse -Force
}
$process = Start-Process -FilePath (Join-Path $testRoot 'QuickCapture.Tests.exe') -ArgumentList $Arguments -WindowStyle Hidden -RedirectStandardError (Join-Path $testRoot 'runner-error.txt') -RedirectStandardOutput (Join-Path $testRoot 'runner-output.txt') -PassThru
$timer = [Diagnostics.Stopwatch]::StartNew()
while (-not $process.WaitForExit(1000)) { if ($timer.Elapsed.TotalMinutes -gt 5) { $process.Kill($true); throw "Targeted checks timed out: $testRoot" } }
if ($Arguments -contains '--self-test') {
    $diagnostics = Join-Path $testRoot 'Diagnostics'
    if (Test-Path -LiteralPath $diagnostics) { Copy-Item -LiteralPath $diagnostics -Destination $source -Recurse -Force }
}
Write-Output $testRoot
if ($process.ExitCode -ne 0) { throw "Targeted checks failed: $testRoot/Diagnostics/results.json" }

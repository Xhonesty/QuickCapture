param([Parameter(Mandatory)][string]$InstallerFile, [Parameter(Mandatory)][string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot 'artifacts'
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
. (Join-Path $PSScriptRoot 'ReleasePayload.ps1')
$installer = Assert-QuickCaptureArtifactPath ([IO.Path]::GetFullPath($InstallerFile, $projectRoot)) $artifactRoot
$payload = [IO.Path]::GetFullPath($SourceDirectory, $projectRoot)
if (-not $payload.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload must stay inside QuickCapture.' }
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/QuickCapture.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$testRoot = Assert-QuickCaptureArtifactPath (Join-Path $artifactRoot ('installer-checks\payload-' + $version + '-' + [guid]::NewGuid().ToString('N'))) $artifactRoot
$app = Join-Path $testRoot 'Application'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$env:TEMP = Join-Path $projectRoot '.tools/temp'; $env:TMP = $env:TEMP
$dllHash = (Get-FileHash -LiteralPath (Join-Path $payload 'QuickCapture.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$readmeHash = (Get-FileHash -LiteralPath (Join-Path $payload 'README.md') -Algorithm SHA256).Hash.ToLowerInvariant()
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [scriptblock]$Action) {
    try { & $Action; $checks.Add(@{name=$Name;passed=$true}) }
    catch { $checks.Add(@{name=$Name;passed=$false;error=$_.Exception.Message}); throw }
}
$history = Join-Path $artifactRoot 'release-history'
New-Item -ItemType Directory -Path $history -Force | Out-Null
$previous = Join-Path $artifactRoot 'installer-verification.json'
if (Test-Path -LiteralPath $previous) {
    $prior = Get-Content -LiteralPath $previous -Raw | ConvertFrom-Json
    if ($prior.version -ne $version) { $priorRecord = Join-Path $history ($prior.version + '-installer-verification.json'); if (-not (Test-Path -LiteralPath $priorRecord)) { Copy-Item -LiteralPath $previous -Destination $priorRecord } }
}
try {
    Check 'Installer checksum and product version match the verified build' {
        $sha = (Get-Content -LiteralPath ($installer + '.sha256') -Raw).Split(' ')[0]
        if ($sha -ne (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -or (Get-Item -LiteralPath $installer).VersionInfo.ProductVersion.Trim() -ne $version) { throw 'Installer checksum/version mismatch.' }
    }
    Check 'Isolated payload installation matches DLL README and required runtime files' {
        $arguments = @('/VALIDATION=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $app + '"'),('/LOG="' + (Join-Path $testRoot 'install.log') + '"'))
        $process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(1000)) { if ($timer.Elapsed.TotalSeconds -gt 180) { Stop-Process -Id $process.Id -Force; throw 'Payload installation timed out.' } }
        if ($process.ExitCode -ne 0) { throw 'Payload installation failed.' }
        $null = Assert-QuickCaptureReleasePayload $app
        foreach ($name in @('QuickCapture.exe','installed-mode','uninsQuickCapture.exe','Tools/media/ffmpeg.exe','Tools/media/ffprobe.exe','Tools/ocr/tessdata/chi_sim.traineddata')) { if (-not (Test-Path -LiteralPath (Join-Path $app $name))) { throw "Missing runtime: $name" } }
        if ((Get-FileHash -LiteralPath (Join-Path $app 'QuickCapture.dll') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $dllHash) { throw 'Installer DLL differs from validated DLL.' }
        if ((Get-FileHash -LiteralPath (Join-Path $app 'README.md') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $readmeHash) { throw 'Installer README differs.' }
    }
    Check 'Installer excludes user data and private maintenance records' {
        foreach ($name in @('Data','Captures','Diagnostics','docs/local')) { if (Test-Path -LiteralPath (Join-Path $app $name)) { throw "Private directory bundled: $name" } }
    }
} finally {
    $result = [ordered]@{version=$version;mode='payload-only';installer=$installer;sha256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant();applicationDllSha256=$dllHash;readmeSha256=$readmeHash;testRoot=$testRoot;validationMode=$true;systemShortcutsAndRegistrationTested=$false;nativeWizardInstallCommitTested=$false;checks=@($checks.ToArray());reusedInstallerBehavior='Installer/uninstaller/directory behavior unchanged; prior 0.12.0 installation upgrade and uninstall checks retained in artifacts/release-history. Historical checks were not rerun.'}
    [IO.File]::WriteAllText((Join-Path $artifactRoot 'installer-verification.json'),($result | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
Write-Output (Join-Path $artifactRoot 'installer-verification.json')

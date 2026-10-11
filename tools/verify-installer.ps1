param([string]$InstallerFile, [string]$PreviousInstallerFile, [string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot 'artifacts'
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\QuickCapture.csproj')
$version = $project.Project.PropertyGroup.Version
$payload = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory, $projectRoot) } else { Join-Path $projectRoot 'dist' }
if (-not $payload.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload verification source must stay inside QuickCapture.' }
$payloadHash = (Get-FileHash -LiteralPath (Join-Path $payload 'QuickCapture.dll') -Algorithm SHA256).Hash
$payloadReadmeHash = (Get-FileHash -LiteralPath (Join-Path $payload 'README.md') -Algorithm SHA256).Hash
$installer = if ($InstallerFile) { [IO.Path]::GetFullPath($InstallerFile, $projectRoot) } else { Join-Path $artifactRoot "QuickCapture-Setup-$version-x64.exe" }
if (-not $installer.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Verify only installer artifacts within QuickCapture.' }
$previousInstaller = if ($PreviousInstallerFile) { [IO.Path]::GetFullPath($PreviousInstallerFile, $projectRoot) } else { $null }
if ($previousInstaller -and (-not $previousInstaller.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $previousInstaller))) { throw 'Previous installer must be an existing artifact within QuickCapture.' }
$testRoot = Join-Path $artifactRoot ('installer-checks\' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $testRoot 'Application 轻截'
$portable = Join-Path $testRoot 'ProtectedPortable'
$taskTemp = Join-Path $projectRoot '.tools\temp'
New-Item -ItemType Directory -Path $testRoot,$taskTemp -Force | Out-Null
$env:TEMP = $taskTemp; $env:TMP = $taskTemp
$checks = [Collections.Generic.List[object]]::new()
function Save-Result {
    $wizardRecord = Join-Path $artifactRoot 'installer-wizard-verification.json'
    $wizardVerified = $false
    if (Test-Path -LiteralPath $wizardRecord) {
        $wizard = Get-Content -LiteralPath $wizardRecord -Raw | ConvertFrom-Json
        $wizardVerified = $wizard.version -eq $version -and $wizard.sourceHashes.script -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'installer\QuickCapture.iss')).Hash -and $wizard.sourceHashes.language -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'installer\languages\ChineseSimplified.isl')).Hash -and $wizard.sourceHashes.information -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'installer\installation-info.txt')).Hash
    }
    $result = [ordered]@{ version = $version; installer = $installer; sha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant(); readmeSha256 = $payloadReadmeHash.ToLowerInvariant(); previousInstaller = $previousInstaller; previousInstallerSha256 = $(if ($previousInstaller) { (Get-FileHash -LiteralPath $previousInstaller -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }); testRoot = $testRoot; validationMode = $true; systemShortcutsAndRegistrationTested = $false; nativeWizardNavigationVerifiedForSameSources = $wizardVerified; nativeWizardInstallCommitTested = $false; checks = @($checks.ToArray()) }
    [IO.File]::WriteAllText((Join-Path $artifactRoot 'installer-verification.json'), ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
function Check([string]$Name, [scriptblock]$Action) {
    Write-Output "Checking: $Name"
    try { & $Action; $checks.Add(@{ name = $Name; passed = $true }) }
    catch { $checks.Add(@{ name = $Name; passed = $false; error = $_.Exception.Message }); Save-Result; throw }
    Save-Result
}
function Assert-PublicDocumentation([string]$Directory) {
    if ((Get-FileHash -LiteralPath (Join-Path $Directory 'README.md') -Algorithm SHA256).Hash -ne $payloadReadmeHash) { throw 'Installed README differs from the current build.' }
    if (Test-Path -LiteralPath (Join-Path $Directory 'docs\local')) { throw 'Local maintenance documents were bundled in the installer.' }
}
function Invoke-Setup([string]$Directory, [string]$LogName, [string]$SetupFile = $installer) {
    $arguments = @('/VALIDATION=1','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=chinesesimp',('/DIR="' + $Directory + '"'),('/LOG="' + (Join-Path $testRoot $LogName) + '"'))
    $process = Start-Process -FilePath $SetupFile -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(180000)) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        throw 'Installer test timed out; its process tree was stopped.'
    }
    return $process.ExitCode
}
function Invoke-NamedUninstall([string]$Directory, [string]$LogName) {
    $uninstallLog = Join-Path $testRoot $LogName
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + $uninstallLog + '"'))
    $process = Start-Process -FilePath (Join-Path $Directory 'uninsQuickCapture.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { & taskkill.exe /PID $process.Id /T /F | Out-Null; throw 'Uninstall test timed out.' }
    if ($process.ExitCode -ne 0) { throw 'Named uninstall launcher failed.' }
    # The launcher exits to release its own EXE; wait for the engine's actual result.
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $log = if (Test-Path -LiteralPath $uninstallLog) { Get-Content -LiteralPath $uninstallLog -Raw } else { '' }
        $remaining = @('QuickCapture.exe','installed-mode','uninsQuickCapture.exe','uninsQuickCapture.ini','unins000.exe','unins000.dat','unins000.msg') | Where-Object { Test-Path -LiteralPath (Join-Path $Directory $_) }
        if ($log -match 'Uninstallation process succeeded\.' -and $log -match 'Log closed\.' -and -not $remaining) { break }
        Start-Sleep -Milliseconds 250
    } while ($timer.Elapsed.TotalSeconds -lt 60)
    if ($log -notmatch 'Uninstallation process succeeded\.' -or $log -notmatch 'Log closed\.') { throw 'Uninstall engine did not report success.' }
    foreach ($name in @('QuickCapture.exe','installed-mode','uninsQuickCapture.exe','uninsQuickCapture.ini','unins000.exe','unins000.dat','unins000.msg')) {
        if (Test-Path -LiteralPath (Join-Path $Directory $name)) { throw "Uninstall retained a program file: $name" }
    }
}
Check 'Runtime architecture and checksum match the build' {
    $expected = (Get-Content -LiteralPath ($installer + '.sha256') -Raw).Split(' ')[0]
    if ($expected -ne (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash) { throw 'Installer checksum mismatch.' }
}
Check 'Running application mutex blocks installation' {
    $mutex = [Threading.Mutex]::new($false, 'Local\QuickCapture.Personal.x64')
    try { if ((Invoke-Setup $app 'running-app.log') -eq 0) { throw 'Running application was overwritten.' } }
    finally { $mutex.Dispose() }
    if (Test-Path -LiteralPath (Join-Path $app 'QuickCapture.exe')) { throw 'Blocked installation wrote application files.' }
}
Check 'Portable directory and its settings are protected' {
    New-Item -ItemType Directory -Path (Join-Path $portable 'Data') -Force | Out-Null
    $preserved = Join-Path $portable 'Data\settings.json'
    [IO.File]::WriteAllText($preserved, '{"Theme":"Light"}')
    $hash = (Get-FileHash -LiteralPath $preserved).Hash
    if ((Invoke-Setup $portable 'portable-block.log') -eq 0) { throw 'Portable directory was accepted.' }
    if ((Get-FileHash -LiteralPath $preserved).Hash -ne $hash -or (Test-Path -LiteralPath (Join-Path $portable 'QuickCapture.exe'))) { throw 'Portable data changed.' }
}
Check 'Fresh installation has current README and no personal or local documents' {
    if ((Invoke-Setup $app 'install.log') -ne 0) { throw 'Fresh installation failed.' }
    foreach ($name in @('QuickCapture.exe','installed-mode','uninsQuickCapture.exe','uninsQuickCapture.ini','unins000.exe','Tools\media\ffmpeg.exe','Tools\media\ffprobe.exe','Tools\ocr\tessdata\chi_sim.traineddata','licenses\InnoSetup-LICENSE.txt','docs\installer.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $app $name))) { throw "Missing installed file: $name" }
    }
    foreach ($name in @('Data','Captures','Diagnostics')) { if (Test-Path -LiteralPath (Join-Path $app $name)) { throw "Personal data bundled: $name" } }
    if ((Get-FileHash -LiteralPath (Join-Path $app 'QuickCapture.dll') -Algorithm SHA256).Hash -ne $payloadHash) { throw 'Installed DLL differs from the same build used for the portable package.' }
    Assert-PublicDocumentation $app
    $log = Get-Content -LiteralPath (Join-Path $testRoot 'install.log') -Raw
    if ($log -notmatch 'Administrative install mode: No' -or $log -notmatch '64-bit install mode: Yes') { throw 'Incorrect install privilege or architecture.' }
}
Check 'Installed application, recovery state and both themes pass diagnostics' {
    & (Join-Path $PSScriptRoot 'test.ps1') -Offline -SourceDirectory $app -Arguments @('--self-test','--installation-only')
    Copy-Item -LiteralPath (Join-Path $app 'Diagnostics\results.json') -Destination (Join-Path $artifactRoot 'installer-app-checks.json')
    $layout = Get-Content -LiteralPath (Join-Path $app 'Diagnostics\installation-layout.json') -Raw | ConvertFrom-Json
    if (-not $layout.installed -or $layout.userDirectoriesWritten) { throw 'Installed layout or isolated diagnostics mismatch.' }
}
$retained = Join-Path $app 'Data\user-file.bin'
New-Item -ItemType Directory -Path (Split-Path -Parent $retained) -Force | Out-Null
[IO.File]::WriteAllBytes($retained, [byte[]](1,2,3,4,5))
$dataHash = (Get-FileHash -LiteralPath $retained).Hash
$settings = Join-Path $app 'Diagnostics\InstallationChecks\Data\settings.json'
$settingsHash = (Get-FileHash -LiteralPath $settings).Hash
Check 'Covering reinstall preserves configuration and user files' {
    if ((Invoke-Setup $app 'reinstall.log') -ne 0) { throw 'Covering reinstall failed.' }
    if ((Get-FileHash -LiteralPath $retained).Hash -ne $dataHash -or (Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) { throw 'Reinstall altered user files.' }
}
Check 'Uninstall removes program files and preserves user data' {
    Invoke-NamedUninstall $app 'uninstall 轻截.log'
    if ((Get-FileHash -LiteralPath $retained).Hash -ne $dataHash -or (Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) { throw 'Uninstall altered retained data.' }
}
if ($previousInstaller) {
    Check 'Previous installer upgrades application and README and preserves data' {
        $legacyApp = Join-Path $testRoot 'LegacyApplication'
        if ((Invoke-Setup $legacyApp 'legacy-install.log' $previousInstaller) -ne 0) { throw 'Previous installer failed.' }
        if (-not (Test-Path -LiteralPath (Join-Path $legacyApp 'unins000.exe'))) { throw 'Previous installation has no native uninstall engine.' }
        $legacyData = Join-Path $legacyApp 'Data\settings.json'
        New-Item -ItemType Directory -Path (Split-Path -Parent $legacyData) -Force | Out-Null
        [IO.File]::WriteAllText($legacyData, '{"Theme":"Dark","SavedBy":"legacy-install-check"}')
        $legacyHash = (Get-FileHash -LiteralPath $legacyData).Hash
        if ((Invoke-Setup $legacyApp 'legacy-upgrade.log') -ne 0) { throw 'Previous installation upgrade failed.' }
        if ((Get-FileHash -LiteralPath (Join-Path $legacyApp 'QuickCapture.dll') -Algorithm SHA256).Hash -ne $payloadHash) { throw 'Upgrade retained an outdated application DLL.' }
        Assert-PublicDocumentation $legacyApp
        if ((Get-FileHash -LiteralPath $legacyData).Hash -ne $legacyHash -or (Get-Content -LiteralPath (Join-Path $legacyApp 'uninsQuickCapture.ini') -Raw) -notmatch 'Executable=unins000.exe') { throw 'Upgrade altered data or the internal uninstall filename.' }
        Invoke-NamedUninstall $legacyApp 'legacy-uninstall.log'
        if ((Get-FileHash -LiteralPath $legacyData).Hash -ne $legacyHash) { throw 'Upgraded uninstall altered retained data.' }
    }
}
Save-Result
Write-Output (Join-Path $artifactRoot 'installer-verification.json')

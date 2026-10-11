param([switch]$Preview, [switch]$AllPrevious)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
. (Join-Path $PSScriptRoot 'ReleaseSupport.ps1')
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\QuickCapture.csproj')
$current = [string]$project.Project.PropertyGroup.Version
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
function Assert-CleanupPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($AllPrevious -and ($full -eq $distRoot -or $full.StartsWith($distRoot + '\', [StringComparison]::OrdinalIgnoreCase))) {
        $cursor = $full
        while ($cursor -and $cursor.Length -ge $distRoot.Length) {
            if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing reparse point: $cursor" }
            $cursor = [IO.Path]::GetDirectoryName($cursor)
        }
        return $full
    }
    return Assert-QuickCaptureArtifactPath $full $artifactRoot
}
$candidates = [Collections.Generic.List[object]]::new()
function Add-Candidate([string]$Path, [string]$Version, [string]$Evidence) {
    $Version = $Version.Trim()
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { return }
    $full = Assert-CleanupPath $Path
    $candidates.Add([pscustomobject]@{ path=$full; version=$Version; evidence=$Evidence })
}
$previousDirectory = Join-Path $artifactRoot 'installer-previous'
if (Test-Path -LiteralPath $previousDirectory) {
    foreach ($file in Get-ChildItem -LiteralPath $previousDirectory -Filter 'QuickCapture-Setup-*-x64.exe' -File) {
        Add-Candidate $file.FullName ($file.VersionInfo.ProductVersion.Split('+')[0]) 'previous installer product version'
    }
}
foreach ($parent in @('backups','publish')) {
    $directory = Join-Path $artifactRoot $parent
    if (Test-Path -LiteralPath $directory) {
        foreach ($folder in Get-ChildItem -LiteralPath $directory -Directory) {
            if ($folder.Name -match '^v(\d+\.\d+\.\d+)$') { Add-Candidate $folder.FullName $Matches[1] 'versioned backup directory' }
        }
    }
}
foreach ($file in Get-ChildItem -LiteralPath $artifactRoot -File) {
    if ($file.Name -like 'QuickCapture*.zip') {
        $version = Get-QuickCaptureArchiveVersion $file.FullName
        if ($version) { Add-Candidate $file.FullName $version 'QuickCapture.deps.json inside ZIP' }
    } elseif ($file.Name -match '^QuickCapture-Setup-(\d+\.\d+\.\d+)-x64\.exe$') {
        if (-not $file.VersionInfo.ProductVersion) { continue } # Preserve unfinished or unversioned installers.
        $version = $file.VersionInfo.ProductVersion.Split('+')[0].Trim()
        if ($version -eq $Matches[1]) { Add-Candidate $file.FullName $version 'installer product version and filename agree' }
    }
}
foreach ($name in @('editable-build','recording-reedit-build','settings-tabs-build','installer-build','ui-refresh')) {
    $folder = Join-Path $artifactRoot $name
    foreach ($relative in @('QuickCapture.exe','app\QuickCapture.exe')) {
        $exe = Join-Path $folder $relative
        if (Test-Path -LiteralPath $exe) { Add-Candidate $folder ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0]) 'compiled application product version'; break }
    }
}
$snapshot = Join-Path $artifactRoot 'task-baseline\head.zip'
if (Test-Path -LiteralPath $snapshot) { Add-Candidate $snapshot (Get-QuickCaptureArchiveVersion $snapshot) 'QuickCapture.csproj inside historical head.zip' }
if ($AllPrevious) {
    function Find-ProgramCopies([string]$Directory) {
        $folder = Get-Item -LiteralPath $Directory -Force
        if ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing reparse point: $Directory" }
        if ($folder.Name -in @('Data','Captures','Recordings','ScreenshotProjects','Diagnostics','screenshots','research-work','research','task-baseline','backups','publish','release-history')) { return }
        $exe = Join-Path $Directory 'QuickCapture.exe'
        if (Test-Path -LiteralPath $exe) {
            Add-Candidate $Directory ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Split('+')[0]) 'compiled application product version (all-previous cleanup)'
            return
        }
        foreach ($child in Get-ChildItem -LiteralPath $Directory -Directory -Force) { Find-ProgramCopies $child.FullName }
    }
    foreach ($folder in Get-ChildItem -LiteralPath $artifactRoot -Directory) { Find-ProgramCopies $folder.FullName }
    if (Test-Path -LiteralPath (Join-Path $distRoot 'QuickCapture.exe')) { Find-ProgramCopies $distRoot }
}
$versions = @($current) + @($candidates | ForEach-Object version) + @(& git -C $projectRoot tag --list | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } | ForEach-Object { $_.Substring(1) })
$keep = if ($AllPrevious) { @($current) } else { @($versions | Where-Object { [version]$_ -le [version]$current } | Sort-Object { [version]$_ } -Descending -Unique | Select-Object -First 4) }
$targets = @($candidates | Where-Object { [version]$_.version -lt [version]$current -and $_.version -notin $keep } | Sort-Object path -Unique)
$removed = [Collections.Generic.List[object]]::new()
$protected = [Collections.Generic.List[object]]::new()
function Remove-Backup([string]$Path) {
    $full = Assert-CleanupPath $Path
    if (-not (Test-Path -LiteralPath $full)) { return }
    $item = Get-Item -LiteralPath $full -Force
    if ($item.PSIsContainer) {
        if ($item.Name -in @('Data','Captures','Recordings','ScreenshotProjects','Diagnostics')) {
            foreach ($file in Get-ChildItem -LiteralPath $full -File -Recurse -Force) {
                $protected.Add([pscustomobject]@{path=$file.FullName;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash})
            }
            return
        }
        foreach ($child in Get-ChildItem -LiteralPath $full -Force) { Remove-Backup $child.FullName }
        if (-not $Preview -and -not (Get-ChildItem -LiteralPath $full -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $full -Force }
    } else {
        $removed.Add([pscustomobject]@{path=$full;bytes=$item.Length})
        if (-not $Preview) { Remove-Item -LiteralPath $full -Force }
    }
}
foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target.path)) { continue }
    # Refuse the complete target if a nested reparse point exists.
    if ((Get-Item -LiteralPath $target.path -Force).PSIsContainer -and (Get-ChildItem -LiteralPath $target.path -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1)) { throw "Backup contains a reparse point: $($target.path)" }
    Remove-Backup $target.path
    $sidecars = @(($target.path + '.sha256'), ($target.path + '.sha256.txt'), ([IO.Path]::ChangeExtension($target.path, '.sha256.txt')))
    foreach ($sidecar in $sidecars | Select-Object -Unique) { if (Test-Path -LiteralPath $sidecar) { Remove-Backup $sidecar } }
}
foreach ($file in $protected) { if ((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Protected data changed: $($file.path)" } }
$report = [ordered]@{currentVersion=$current;retainedVersions=$keep;allPrevious=[bool]$AllPrevious;preview=[bool]$Preview;targets=$targets;removedFiles=@($removed.ToArray());removedBytes=($removed | Measure-Object bytes -Sum).Sum;protectedFiles=@($protected.ToArray());unknownSourceBackupsRetained=@('artifacts/task-baseline loose source files and existing.patch');completedAt=(Get-Date -Format o)}
$output = Join-Path $artifactRoot $(if ($AllPrevious) { if ($Preview) {'old-version-cleanup-preview.json'} else {'old-version-cleanup-results.json'} } elseif ($Preview) {'backup-cleanup-preview.json'} else {'backup-cleanup-results.json'})
[IO.File]::WriteAllText($output, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Output ([pscustomobject]$report | Select-Object currentVersion,retainedVersions,preview,removedBytes | ConvertTo-Json)

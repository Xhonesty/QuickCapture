param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'installer\uninstall-launcher'
$output = Join-Path $projectRoot 'artifacts\uninstall-launcher'
$taskTemp = Join-Path $projectRoot '.tools\temp'
New-Item -ItemType Directory -Path $output,$taskTemp -Force | Out-Null
$env:TEMP = $taskTemp; $env:TMP = $taskTemp
$gcc = (Get-Command gcc.exe -ErrorAction Stop).Source
$windres = (Get-Command windres.exe -ErrorAction Stop).Source
if ((& $gcc '-dumpmachine') -ne 'x86_64-w64-mingw32') { throw 'The uninstall launcher requires a MinGW-w64 x64 compiler.' }
$resource = Join-Path $output 'launcher.o'
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src\QuickCapture.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$versionNumbers = [version]$version
$numericVersion = @($versionNumbers.Major,$versionNumbers.Minor,[Math]::Max(0,$versionNumbers.Build),[Math]::Max(0,$versionNumbers.Revision)) -join ','
$resourceScript = Join-Path $output 'launcher.rc'
$template = (Get-Content -LiteralPath (Join-Path $source 'launcher.rc') -Raw).Replace('@VERSION_NUMERIC@',$numericVersion).Replace('@VERSION_TEXT@',$version)
[IO.File]::WriteAllText($resourceScript, $template, [Text.UTF8Encoding]::new($false))
& $windres ("--include-dir=$source") ("--include-dir=" + (Join-Path $projectRoot 'src\Assets')) '-i' $resourceScript '-o' $resource
if ($LASTEXITCODE -ne 0) { throw 'Uninstall launcher resources failed to compile.' }
$executable = Join-Path $output 'uninsQuickCapture.exe'
& $gcc '-Os' '-Wall' '-Wextra' '-Werror' '-municode' '-mwindows' '-static-libgcc' '-s' '-Wl,--dynamicbase,--nxcompat,--high-entropy-va' (Join-Path $source 'launcher.c') $resource '-o' $executable
if ($LASTEXITCODE -ne 0) { throw 'Uninstall launcher failed to compile.' }
Write-Output $executable

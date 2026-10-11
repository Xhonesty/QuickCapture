param([switch]$Offline, [string]$Python = 'python', [string]$Bash)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Bash) { $Bash = Join-Path $env:ProgramFiles 'Git\bin\bash.exe' }
if (-not (Test-Path -LiteralPath $Bash)) { throw 'Git Bash is required to build the pinned shared FFmpeg libraries.' }
if ((& gcc '-dumpmachine') -ne 'x86_64-w64-mingw32') { throw 'MinGW-w64 x64 GCC is required.' }
$env:QUICKCAPTURE_MINGW_BIN = Split-Path -Parent (Get-Command gcc -ErrorAction Stop).Source
$prepareArgs = @((Join-Path $PSScriptRoot 'build-media.py'), 'prepare')
if ($Offline) { $prepareArgs += '--offline' }
& $Python @prepareArgs
if ($LASTEXITCODE -ne 0) { throw 'Media source preparation failed.' }
& $Bash '--noprofile' '--norc' (Join-Path $PSScriptRoot 'build-media.sh')
if ($LASTEXITCODE -ne 0) { throw 'Shared FFmpeg build failed. See .tools/media-native/configure.log and compile.log.' }
& $Python (Join-Path $PSScriptRoot 'build-media.py') 'collect'
if ($LASTEXITCODE -ne 0) { throw 'Media runtime collection failed.' }

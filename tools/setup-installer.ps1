param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$downloadRoot = Join-Path $projectRoot '.tools\installer-downloads'
$compilerRoot = Join-Path $projectRoot '.tools\inno'
$download = Join-Path $downloadRoot 'innosetup-7.1.0-x64.exe'
$expected = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $download)) {
    Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe?quickcapture=7.1.0' -OutFile $download
}
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expected) { throw 'Inno Setup download checksum mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*O=Pyrsys B.V.*') { throw 'Inno Setup publisher signature verification failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $compilerRoot 'ISCC.exe'))) {
    $taskTemp = Join-Path $projectRoot '.tools\temp'
    New-Item -ItemType Directory -Path $taskTemp -Force | Out-Null
    $env:TEMP = $taskTemp; $env:TMP = $taskTemp
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/PORTABLE=1','/NOICONS',('/DIR="' + $compilerRoot + '"'),('/LOG="' + (Join-Path $downloadRoot 'inno-portable.log') + '"'))
    $process = Start-Process -FilePath $download -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Inno Setup portable extraction failed: $($process.ExitCode)" }
}
$compiler = Join-Path $compilerRoot 'ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Inno Setup compiler is missing.' }
if ((& $compiler '--version') -ne '7.1.0') { throw 'Unexpected Inno Setup compiler version.' }
Write-Output $compiler

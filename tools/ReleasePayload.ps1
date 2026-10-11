# Both package formats consume the same explicit, checksum-verified payload.
function Assert-QuickCaptureReleasePayload([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $manifestPath = Join-Path $root 'payload-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Missing clean publish manifest; build a fresh payload first.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if (-not $manifest.files -or $manifest.schema -ne 1) { throw 'Invalid publish manifest.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        $relative = [string]$file.path
        if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[/\\])\.\.([/\\]|$)|:|^(Data|Captures|Diagnostics|tests|docs/local)([/\\]|$)|QuickCapture\.Tests|\.pdb$') { throw "Invalid runtime payload path: $relative" }
        $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $names.Add($relative)) { throw 'Invalid or duplicate payload path.' }
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint -or $item.Length -ne $file.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Runtime payload checksum mismatch: $relative" }
    }
    return $manifest
}

function Copy-QuickCaptureReleasePayload([string]$Source, [string]$Destination) {
    $manifest = Assert-QuickCaptureReleasePayload $Source
    foreach ($file in $manifest.files) {
        $target = Join-Path $Destination $file.path
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Source $file.path) -Destination $target
    }
    Copy-Item -LiteralPath (Join-Path $Source 'payload-manifest.json') -Destination $Destination
}

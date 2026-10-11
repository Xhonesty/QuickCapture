function Get-QuickCaptureArchiveVersion([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.Name -eq 'QuickCapture.deps.json' } | Select-Object -First 1
        if ($entry) {
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $doc = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            $key = $doc.libraries.PSObject.Properties.Name | Where-Object { $_ -match '^QuickCapture/(\d+\.\d+\.\d+)$' } | Select-Object -First 1
            if ($key) { return $key.Split('/')[1] }
        }
        # Only used for the explicitly named historical source snapshot.
        $entry = $zip.Entries | Where-Object { $_.Name -eq 'QuickCapture.csproj' } | Select-Object -First 1
        if ($entry) {
            $reader = [IO.StreamReader]::new($entry.Open())
            try { [xml]$project = $reader.ReadToEnd() } finally { $reader.Dispose() }
            return [string]$project.Project.PropertyGroup.Version
        }
    } finally { $zip.Dispose() }
    return $null
}
function Assert-QuickCaptureArtifactPath([string]$Path, [string]$ArtifactRoot) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($ArtifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside QuickCapture/artifacts: $full" }
    $cursor = $full
    while ($cursor -and $cursor.Length -gt $ArtifactRoot.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing reparse point: $cursor" }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

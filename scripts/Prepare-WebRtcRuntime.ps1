$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cache = Join-Path $root 'build/webrtc-runtime'
$output = Join-Path $root 'build/native-webrtc'
$expectedHash = 'B56A9101F706F9D95F815F5B7FA6EFBAC972E86573D378B96A07CFF5540C5961'
New-Item -ItemType Directory -Force -Path $cache, $output | Out-Null
$archive = Join-Path $cache 'Microsoft.VCLibs.x64.14.00.Desktop.appx'
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Microsoft runtime archive hash changed. Review the new signed package before updating the pinned hash.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($name in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        $entry = $zip.Entries | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if (!$entry) { throw "Missing runtime library: $name" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $output $name), $true)
    }
} finally { $zip.Dispose() }
Write-Output 'Prepared pinned Microsoft VC++ x64 runtime 14.39.33321.0 for the portable WebRTC build (no installation).'

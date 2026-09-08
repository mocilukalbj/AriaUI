# Requires PowerShell 7 (.NET cryptography APIs). CRX3 specification:
# https://github.com/chromium/chromium/blob/main/components/crx_file/crx3.proto
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$KeyPath
)
$ErrorActionPreference = 'Stop'
$taskSource = Join-Path $PSScriptRoot '..\undone_plugin'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
$taskKeyPath = [IO.Path]::GetFullPath($KeyPath)
if ($taskKeyPath.StartsWith($taskOutput.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep the signing private key outside the distribution directory.' }
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $taskKeyPath) -Force | Out-Null
$taskKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    if (Test-Path -LiteralPath $taskKeyPath) { $taskKey.ImportFromPem([IO.File]::ReadAllText($taskKeyPath)) }
    else { [IO.File]::WriteAllText($taskKeyPath, $taskKey.ExportPkcs8PrivateKeyPem()) }
    [byte[]]$taskPublic = $taskKey.ExportSubjectPublicKeyInfo()
    [byte[]]$taskIdBytes = [Security.Cryptography.SHA256]::HashData($taskPublic)[0..15]
    $taskId = -join ($taskIdBytes | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
    $taskManifest = Get-Content -LiteralPath (Join-Path $taskSource 'manifest.json') -Raw | ConvertFrom-Json
    $taskManifest | Add-Member -NotePropertyName key -NotePropertyValue ([Convert]::ToBase64String($taskPublic)) -Force
    $taskStem = "AriaUI-Companion-$($taskManifest.version)"
    $taskZipPath = Join-Path $taskOutput "$taskStem.zip"
    $taskStream = [IO.File]::Create($taskZipPath)
    $taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $taskEntry = $taskZip.CreateEntry('manifest.json').Open()
        try { $taskBytes = [Text.Encoding]::UTF8.GetBytes(($taskManifest | ConvertTo-Json -Depth 20)); $taskEntry.Write($taskBytes) } finally { $taskEntry.Dispose() }
        $taskFiles = @('background.js','options.html','README.md','README.cn.md','LINUX_TESTING.md','LICENSE','acknowledgment.txt','css/companion.css','images/logo16.png','images/logo32.png','images/logo48.png','images/logo128.png')
        $taskFiles += Get-ChildItem -LiteralPath (Join-Path $taskSource 'js') -Filter '*.js' -File | ForEach-Object { 'js/' + $_.Name }
        foreach ($taskFile in $taskFiles) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, (Join-Path $taskSource $taskFile), $taskFile) | Out-Null }
    } finally { $taskZip.Dispose(); $taskStream.Dispose() }
    function Varint([uint32]$value) {
        $result = [Collections.Generic.List[byte]]::new()
        while ($value -ge 128) { $result.Add([byte](($value -band 127) -bor 128)); $value = $value -shr 7 }
        $result.Add([byte]$value)
        return ,$result.ToArray()
    }
    function Field([uint32]$number, [byte[]]$value) { return ,([byte[]]((Varint (($number -shl 3) -bor 2)) + (Varint $value.Length) + $value)) }
    [byte[]]$taskSignedHeader = Field 1 $taskIdBytes
    [byte[]]$taskArchive = [IO.File]::ReadAllBytes($taskZipPath)
    [byte[]]$taskSigned = [Text.Encoding]::ASCII.GetBytes("CRX3 SignedData`0") + [BitConverter]::GetBytes([uint32]$taskSignedHeader.Length) + $taskSignedHeader + $taskArchive
    $taskSignature = $taskKey.SignData($taskSigned, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
    $taskVerifier = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $taskRead = 0
        $taskVerifier.ImportSubjectPublicKeyInfo($taskPublic, [ref]$taskRead)
        if (-not $taskVerifier.VerifyData($taskSigned, $taskSignature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) { throw 'CRX signature verification failed.' }
    } finally { $taskVerifier.Dispose() }
    [byte[]]$taskProof = (Field 1 $taskPublic) + (Field 2 $taskSignature)
    [byte[]]$taskHeader = (Field 3 $taskProof) + (Field 10000 $taskSignedHeader)
    [byte[]]$taskCrx = [Text.Encoding]::ASCII.GetBytes('Cr24') + [BitConverter]::GetBytes([uint32]3) + [BitConverter]::GetBytes([uint32]$taskHeader.Length) + $taskHeader + $taskArchive
    [IO.File]::WriteAllBytes((Join-Path $taskOutput "$taskStem.crx"), $taskCrx)
    [IO.File]::WriteAllText((Join-Path $taskOutput 'extension-id.txt'), $taskId + [Environment]::NewLine)
    Write-Output "CRX3 signature verified. Extension ID: $taskId"
} finally { $taskKey.Dispose() }

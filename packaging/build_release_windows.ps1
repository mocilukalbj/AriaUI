[CmdletBinding()]
param(
    [string]$MsysPrefix = 'C:\msys64\ucrt64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\publish\win-x64'),
    [switch]$NativeAot,
    [string]$NuGetConfig
)
$ErrorActionPreference = 'Stop'
$taskRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'build_native_windows.ps1') -MsysPrefix $MsysPrefix
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskArguments = @('-c','Release','-r','win-x64','--self-contained','true','-o',$taskOutput,
    '-p:PublishSingleFile=false',"-p:PublishAot=$($NativeAot.IsPresent.ToString().ToLowerInvariant())")
if ($NuGetConfig) { $taskArguments += "-p:RestoreConfigFile=$([IO.Path]::GetFullPath($NuGetConfig))" }
foreach ($taskProject in @('AriaUI.csproj','tools\AriaUI.Host\AriaUI.Host.csproj')) {
    & dotnet publish (Join-Path $taskRepo $taskProject) @taskArguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $taskProject" }
}
foreach ($taskFile in @('AriaUI.exe','AriaUI.Host.exe','runtimes\win-x64\native\aria2_bridge.dll','runtimes\win-x64\native\ossl-modules\legacy.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskOutput $taskFile))) { throw "Published file missing: $taskFile" }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'register_host_windows.ps1') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRepo 'WINDOWS.md') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSES') -Destination $taskOutput -Recurse -Force
$taskThirdPartyLicenses = Join-Path $MsysPrefix 'share\licenses'
if (Test-Path -LiteralPath $taskThirdPartyLicenses) {
    Copy-Item -LiteralPath $taskThirdPartyLicenses -Destination (Join-Path $taskOutput 'LICENSES\MSYS2') -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SOURCE_REFERENCES.md') -Destination $taskOutput -Force
$taskSource = Join-Path $taskOutput 'source\bridge'
New-Item -ItemType Directory -Path $taskSource -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRepo 'native\bridge\aria2_bridge.cpp'),(Join-Path $taskRepo 'native\bridge\aria2_bridge.h') -Destination $taskSource -Force
Get-ChildItem -LiteralPath $taskOutput -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS' } | ForEach-Object {
    $taskHash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    "$($taskHash.Hash.ToLowerInvariant())  $($_.FullName.Substring($taskOutput.Length + 1).Replace('\','/'))"
} | Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS') -Encoding ASCII
Write-Host "Windows package ready: $taskOutput"

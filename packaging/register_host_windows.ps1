[CmdletBinding()]
param(
    [ValidateSet('Install','Uninstall')][string]$Action = 'Install',
    [ValidateSet('Chrome','Chromium','Edge')][string[]]$Browser = @('Chrome'),
    [string]$HostPath = (Join-Path $PSScriptRoot 'AriaUI.Host.exe'),
    [string]$ExtensionId
)
$ErrorActionPreference = 'Stop'
$taskKeys = @{
    Chrome = 'Software\Google\Chrome\NativeMessagingHosts\com.ariaui.downloader'
    Chromium = 'Software\Chromium\NativeMessagingHosts\com.ariaui.downloader'
    Edge = 'Software\Microsoft\Edge\NativeMessagingHosts\com.ariaui.downloader'
}
if ($Action -eq 'Install') {
    if ($ExtensionId -notmatch '^[a-p]{32}$') { throw 'Provide the actual 32-character extension ID from the browser extension page.' }
    $taskHost = (Resolve-Path -LiteralPath $HostPath).Path
    if ([IO.Path]::GetFileName($taskHost) -ne 'AriaUI.Host.exe') { throw 'HostPath must point to AriaUI.Host.exe.' }
    $taskDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AriaUI\NativeMessagingHosts'
    New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
    $taskManifestData = @{ name = 'com.ariaui.downloader'; description = 'AriaUI Native Messaging Host'; path = $taskHost;
        type = 'stdio'; allowed_origins = @("chrome-extension://$ExtensionId/") } | ConvertTo-Json
}
# Chromium checks the 32-bit view first; use the same per-user view on install/uninstall.
$taskRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry32)
try {
    foreach ($taskBrowser in $Browser) {
        if ($Action -eq 'Install') {
            $taskManifest = Join-Path $taskDirectory "$taskBrowser-com.ariaui.downloader.json"
            $taskManifestData | Set-Content -LiteralPath $taskManifest -Encoding UTF8
            $taskKey = $taskRegistry.CreateSubKey($taskKeys[$taskBrowser])
            try { $taskKey.SetValue('', $taskManifest, [Microsoft.Win32.RegistryValueKind]::String) }
            finally { $taskKey.Dispose() }
        } else {
            $taskRegistry.DeleteSubKey($taskKeys[$taskBrowser], $false)
        }
        Write-Host "$Action completed for $taskBrowser (current user only)."
    }
} finally { $taskRegistry.Dispose() }

[CmdletBinding()]
param(
    [string]$MsysPrefix = 'C:\msys64\ucrt64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\runtimes\win-x64\native')
)
$ErrorActionPreference = 'Stop'
$taskPrefix = (Resolve-Path -LiteralPath $MsysPrefix).Path
$taskBin = Join-Path $taskPrefix 'bin'
$taskCompiler = Join-Path $taskBin 'g++.exe'
$taskObjdump = Join-Path $taskBin 'objdump.exe'
foreach ($taskRequired in @($taskCompiler, $taskObjdump, (Join-Path $taskPrefix 'include\aria2\aria2.h'), (Join-Path $taskBin 'libaria2-0.dll'))) {
    if (-not (Test-Path -LiteralPath $taskRequired)) { throw "Missing $taskRequired. Install UCRT64 gcc and aria2 packages; see WINDOWS.md." }
}
$taskOldPath = $env:PATH
try {
    $env:PATH = $taskBin + ';' + $taskOldPath
    $taskTriple = & $taskCompiler -dumpmachine
    if ($LASTEXITCODE -ne 0 -or $taskTriple -ne 'x86_64-w64-mingw32') { throw 'Only the Windows x64 UCRT64 compiler is supported by this script.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $taskOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
    $taskBridge = Join-Path $taskOutput 'aria2_bridge.dll'
    & $taskCompiler -shared -O2 -std=c++14 -DARIA2_BRIDGE_BUILD '-Wl,--no-undefined' `
        "-I$taskPrefix\include" (Join-Path $PSScriptRoot '..\native\bridge\aria2_bridge.cpp') `
        "-L$taskPrefix\lib" -laria2 -lcrypto -o $taskBridge
    if ($LASTEXITCODE -ne 0) { throw 'Bridge compilation failed.' }

    # Copy the transitive import closure, not the compiler or every DLL in MSYS2.
    $taskQueue = [Collections.Generic.Queue[string]]::new()
    $taskQueue.Enqueue($taskBridge)
    # aria2 loads OpenSSL's legacy provider dynamically; it is absent from PE imports.
    $taskProvider = Join-Path $taskPrefix 'lib\ossl-modules\legacy.dll'
    if (-not (Test-Path -LiteralPath $taskProvider)) { throw 'OpenSSL legacy provider is missing.' }
    New-Item -ItemType Directory -Path (Join-Path $taskOutput 'ossl-modules') -Force | Out-Null
    Copy-Item -LiteralPath $taskProvider -Destination (Join-Path $taskOutput 'ossl-modules\legacy.dll') -Force
    $taskQueue.Enqueue($taskProvider)
    $taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($taskQueue.Count -gt 0) {
        $taskLibrary = $taskQueue.Dequeue()
        $taskImports = & $taskObjdump -p $taskLibrary
        if ($LASTEXITCODE -ne 0) { throw "Cannot inspect $taskLibrary" }
        foreach ($taskLine in $taskImports) {
            if ($taskLine -match 'DLL Name:\s*(\S+)') {
                $taskName = $Matches[1]
                if (-not $taskSeen.Add($taskName)) { continue }
                $taskDependency = Join-Path $taskBin $taskName
                if (Test-Path -LiteralPath $taskDependency) {
                    Copy-Item -LiteralPath $taskDependency -Destination (Join-Path $taskOutput $taskName) -Force
                    $taskQueue.Enqueue($taskDependency)
                } elseif ($taskName -notmatch '^(api-ms-|ext-ms-)' -and
                    -not (Test-Path -LiteralPath (Join-Path $env:SystemRoot "System32\$taskName"))) {
                    throw "Unresolved DLL dependency: $taskName"
                }
            }
        }
    }
    $taskExports = (& $taskObjdump -p $taskBridge) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect bridge exports.' }
    $taskHeader = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\native\bridge\aria2_bridge.h') -Raw
    foreach ($taskSymbol in [regex]::Matches($taskHeader, 'A2_API\s+\w+\s+(a2_\w+)\(')) {
        if ($taskExports -notmatch ('\b' + $taskSymbol.Groups[1].Value + '\b')) { throw "Missing export: $($taskSymbol.Groups[1].Value)" }
    }
    Get-ChildItem -LiteralPath $taskOutput -Filter '*.dll' -Recurse | ForEach-Object {
        $taskHash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        "$($taskHash.Hash.ToLowerInvariant())  $($_.FullName.Substring($taskOutput.Length + 1).Replace('\','/'))"
    } | Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS') -Encoding ASCII
    Write-Host "Windows bridge and its DLL dependencies are ready: $taskOutput"
} finally { $env:PATH = $taskOldPath }

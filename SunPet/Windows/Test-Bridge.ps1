[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'build\bridge-tests'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$exe = Join-Path $destination 'PetBridgeTests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /codepage:65001 /utf8output ('/out:' + $exe) ('/reference:' + (Join-Path $framework 'System.Runtime.Serialization.dll')) (Join-Path $PSScriptRoot 'PetBridge.cs') (Join-Path $PSScriptRoot 'Tests\PetBridgeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Bridge test compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Bridge tests failed.' }

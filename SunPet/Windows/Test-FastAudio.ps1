[CmdletBinding()]
param([string]$AssetsDirectory)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'build\audio-tests'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$exe = Join-Path $destination 'FastAudioTests.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /utf8output /codepage:65001 ('/out:' + $exe) (Join-Path $PSScriptRoot 'FastAudio.cs') (Join-Path $PSScriptRoot 'Tests\FastAudioTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Audio test compilation failed.' }
if ($AssetsDirectory) { & $exe $AssetsDirectory } else { & $exe }
if ($LASTEXITCODE -ne 0) { throw 'Audio tests failed.' }

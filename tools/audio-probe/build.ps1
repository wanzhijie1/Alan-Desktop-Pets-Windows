$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /utf8output /codepage:65001 ('/out:' + (Join-Path $PSScriptRoot 'AudioProbe.exe')) (Join-Path $PSScriptRoot 'AudioProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'AudioProbe compilation failed.' }

[CmdletBinding()]
param([switch]$SkipBuild, [switch]$MediaSmoke)
$ErrorActionPreference = 'Stop'

function Invoke-CheckedExecutable([string]$Executable, [string]$Arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = $Arguments
    $start.WorkingDirectory = Split-Path -Parent $Executable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw ('Could not start ' + $Executable) }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(90000)) {
            $process.Kill()
            $process.WaitForExit()
            throw ('Test timed out: ' + $Executable)
        }
        if ($stdout.Result) { Write-Host $stdout.Result.TrimEnd() }
        if ($stderr.Result) { Write-Host $stderr.Result.TrimEnd() }
        if ($process.ExitCode -ne 0) { throw ('Test failed: ' + $Executable + ' ' + $Arguments) }
    } finally { $process.Dispose() }
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-all.ps1') -SelfTest
} else {
    foreach ($pet in @('SunPet', 'GoodThingPet', 'ShaolanPet')) {
        Invoke-CheckedExecutable (Join-Path $PSScriptRoot ($pet + '\build\windows\' + $pet + '.exe')) '--self-test'
    }
}

# All pets share these engine and bridge implementations; run their suites once.
& (Join-Path $PSScriptRoot 'SunPet\Windows\Test-FastAudio.ps1')
& (Join-Path $PSScriptRoot 'SunPet\Windows\Test-Bridge.ps1')
& (Join-Path $PSScriptRoot 'tests\Test-Trio.ps1')

if ($MediaSmoke) {
    foreach ($pet in @('SunPet', 'GoodThingPet', 'ShaolanPet')) {
        Invoke-CheckedExecutable (Join-Path $PSScriptRoot ($pet + '\build\windows\' + $pet + '.exe')) '--smoke-test'
    }
}
Write-Host 'PASS all requested checks'

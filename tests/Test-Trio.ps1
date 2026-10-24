[CmdletBinding()]
param(
    [string]$SunPetExecutable,
    [string]$FengGeExecutable,
    [Alias('ShaolanExe')][string]$ShaolanExecutable
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $SunPetExecutable) { $SunPetExecutable = Join-Path $repoRoot 'SunPet\build\windows\SunPet.exe' }
if (-not $FengGeExecutable) { $FengGeExecutable = Join-Path $repoRoot 'GoodThingPet\build\windows\GoodThingPet.exe' }
if (-not $ShaolanExecutable) { $ShaolanExecutable = Join-Path $repoRoot 'ShaolanPet\build\windows\ShaolanPet.exe' }
$testRoot = Join-Path $repoRoot 'build\test-results\trio'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$cases = @(
    @{Name='full-chain'; Root='shaolan'; Clip=1; Roles=@('sunpet','fengge','shaolan'); Sun='1,3'; Feng='2'},
    @{Name='fengge-root'; Root='fengge'; Clip=3; Roles=@('sunpet','fengge','shaolan'); Sun='1'; Feng='0'},
    @{Name='sunpet-root'; Root='sunpet'; Clip=13; Roles=@('sunpet','fengge','shaolan'); Sun='0'; Feng=''},
    @{Name='two-pets'; Root='shaolan'; Clip=1; Roles=@('sunpet','shaolan'); Sun='1'; Feng=''},
    @{Name='cancel'; Root='fengge'; Clip=3; Roles=@('sunpet','fengge','shaolan'); Sun=''; Feng='0'; Stop='450'}
)
$executables = @{sunpet=$SunPetExecutable; fengge=$FengGeExecutable; shaolan=$ShaolanExecutable}
foreach ($case in $cases) {
    $scope = 'trio-test-' + [Guid]::NewGuid().ToString('N')
    $processes = @{}
    $logs = @{}
    try {
        # Start listeners before the pet that initiates the chain.
        $roles = @($case.Roles | Where-Object { $_ -ne $case.Root }) + @($case.Root)
        foreach ($role in $roles) {
            $arguments = @('--bridge-test',$scope,'--test-exit-after','8','--trace')
            if ($role -eq $case.Root) {
                $arguments += @('--test-play',[string]$case.Clip)
                if ($case.ContainsKey('Stop')) { $arguments += @('--test-stop-after',$case.Stop) }
            }
            $log = Join-Path $testRoot ($case.Name + '-' + $role + '.log')
            $logs[$role] = $log
            $processes[$role] = Start-Process -FilePath $executables[$role] -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
            [void]$processes[$role].Handle
        }
        foreach ($role in $roles) {
            $process = $processes[$role]
            if (-not $process.WaitForExit(15000)) { throw ($case.Name + ': process timed out: ' + $role) }
            if ($process.ExitCode -ne 0) { throw ($case.Name + ': process failed: ' + $role + ' exit=' + $process.ExitCode) }
            if ((Get-Item -LiteralPath ($logs[$role] + '.err')).Length -ne 0) { throw ($case.Name + ': unexpected stderr: ' + $role) }
        }
        $sunText = if ($logs.ContainsKey('sunpet')) { Get-Content -LiteralPath $logs.sunpet -Raw } else { '' }
        $fengText = if ($logs.ContainsKey('fengge')) { Get-Content -LiteralPath $logs.fengge -Raw } else { '' }
        $sunHops = @([regex]::Matches($sunText, 'playing clip=\d+ hop=(\d+)') | ForEach-Object { $_.Groups[1].Value }) -join ','
        $fengHops = @([regex]::Matches($fengText, 'playing clip=\d+ hop=(\d+)') | ForEach-Object { $_.Groups[1].Value }) -join ','
        if ($sunHops -ne $case.Sun -or $fengHops -ne $case.Feng) {
            throw ($case.Name + ': wrong chain; Sun=' + $sunHops + ', Feng=' + $fengHops)
        }
        if ($case.Name -eq 'cancel' -and $sunText -notmatch 'received stopped from=fengge') { throw 'Cancellation did not reach SunPet.' }
        Write-Output ('PASS ' + $case.Name + ': Sun=[' + $sunHops + '] Feng=[' + $fengHops + ']')
    } finally {
        foreach ($process in $processes.Values) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
    }
}
Write-Output 'PASS all live-process chain cases; no repeated cycles'

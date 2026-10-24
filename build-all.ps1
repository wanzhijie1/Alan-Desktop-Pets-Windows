[CmdletBinding()]
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
foreach ($pet in @('SunPet', 'GoodThingPet', 'ShaolanPet')) {
    Write-Host ('Building ' + $pet)
    & (Join-Path $PSScriptRoot ($pet + '\Windows\build.ps1')) -SelfTest:$SelfTest
}
Write-Host 'All three Windows applications are ready in their build/windows directories.'

[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'build\windows'
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot 'csc.exe'))) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework 4.8 is required. Install it from https://dotnet.microsoft.com/download/dotnet-framework/net48'
}

$frameworkInfo = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if (-not $frameworkInfo -or $frameworkInfo.Release -lt 528040) {
    throw '.NET Framework 4.8 or later is required to build and run GoodThingPet.'
}
$sourceFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Sort-Object Name)
if ($sourceFiles.Count -eq 0) { throw 'No C# source files were found in Windows/.' }

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$assetRoot = Join-Path $outputRoot 'Assets'
New-Item -ItemType Directory -Path (Join-Path $assetRoot 'Clips') -Force | Out-Null
foreach ($asset in @('face.png', 'clips.json')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot ('Assets\' + $asset)) -Destination $assetRoot -Force
}
# These WAV clips are loaded into PCM memory by the application at startup.
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Assets\Clips') -Filter '*.wav' -File |
    Copy-Item -Destination (Join-Path $assetRoot 'Clips') -Force
foreach ($license in @('LICENSE', 'LICENSE-SunPet')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $license) -Destination $outputRoot -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-Windows.txt') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'GoodThingPet.exe.config') -Destination $outputRoot -Force

# Create a small Windows icon from the existing transparent character image.
Add-Type -AssemblyName System.Drawing
if (-not ('GoodThingPetBuild.NativeIcon' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace GoodThingPetBuild {
    public static class NativeIcon {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
'@
}
$iconPath = Join-Path $outputRoot 'GoodThingPet.ico'
$sourceImage = [System.Drawing.Image]::FromFile((Join-Path $assetRoot 'face.png'))
$iconBitmap = New-Object System.Drawing.Bitmap 64, 64
$graphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
$iconHandle = [IntPtr]::Zero
$icon = $null
$iconStream = $null
try {
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $scale = [Math]::Min(64.0 / $sourceImage.Width, 64.0 / $sourceImage.Height)
    $width = [int][Math]::Round($sourceImage.Width * $scale)
    $height = [int][Math]::Round($sourceImage.Height * $scale)
    $rect = New-Object System.Drawing.Rectangle ([int]((64 - $width) / 2)), ([int]((64 - $height) / 2)), $width, $height
    $graphics.DrawImage($sourceImage, $rect)
    $iconHandle = $iconBitmap.GetHicon()
    $icon = [System.Drawing.Icon]::FromHandle($iconHandle)
    $iconStream = [System.IO.File]::Create($iconPath)
    $icon.Save($iconStream)
} finally {
    if ($iconStream) { $iconStream.Dispose() }
    if ($icon) { $icon.Dispose() }
    if ($iconHandle -ne [IntPtr]::Zero) { [GoodThingPetBuild.NativeIcon]::DestroyIcon($iconHandle) | Out-Null }
    $graphics.Dispose()
    $iconBitmap.Dispose()
    $sourceImage.Dispose()
}

$exePath = Join-Path $outputRoot 'GoodThingPet.exe'
$compilerArguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/debug-',
    '/utf8output', '/codepage:65001',
    ('/out:' + $exePath),
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/win32icon:' + $iconPath)
)
foreach ($assembly in @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xaml.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Runtime.Serialization.dll')) {
    $compilerArguments += '/reference:' + (Join-Path $frameworkRoot $assembly)
}
foreach ($assembly in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll')) {
    $compilerArguments += '/reference:' + (Join-Path $frameworkRoot ('WPF\' + $assembly))
}
$compilerArguments += @($sourceFiles | ForEach-Object { $_.FullName })
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
Write-Host ('Built: ' + $exePath)

if ($SelfTest) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exePath
    $startInfo.Arguments = '--self-test'
    $startInfo.WorkingDirectory = $outputRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'Could not start the self-test.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill()
            throw 'Self-test timed out after 60 seconds.'
        }
        $standardOutput = $stdout.Result
        $standardError = $stderr.Result
        if ($standardOutput) { Write-Host $standardOutput.TrimEnd() }
        if ($standardError) { Write-Host $standardError.TrimEnd() }
        if ($process.ExitCode -ne 0) { throw "Self-test failed with exit code $($process.ExitCode)." }
    } finally {
        $process.Dispose()
    }
}

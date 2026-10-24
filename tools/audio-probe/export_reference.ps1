param([string]$AssemblyPath, [string]$AudioPath, [string]$OutputPrefix)
$ErrorActionPreference = 'Stop'
$assembly = [System.Reflection.Assembly]::LoadFrom([System.IO.Path]::GetFullPath($AssemblyPath))
$clipType = $assembly.GetType('DesktopPetAudio.WaveClip', $true)
$clip = $clipType.GetMethod('Load').Invoke($null, [object[]]@([System.IO.Path]::GetFullPath($AudioPath), $true))
$samples = [System.Int16[]]$clipType.GetMethod('CopySamples').Invoke($clip, $null)
$bytes = New-Object byte[] ($samples.Length * 2)
[Buffer]::BlockCopy($samples, 0, $bytes, 0, $bytes.Length)
[System.IO.File]::WriteAllBytes($OutputPrefix + '.pcm', $bytes)
$details = [ordered]@{
    duration = $clip.Duration
    speech_start = $clip.SpeechStart
    speech_end = $clip.SpeechEnd
    leading_trim = $clip.LeadingTrim
    trailing_trim = $clip.TrailingTrim
    source = $clip.SourcePath
}
[System.IO.File]::WriteAllText($OutputPrefix + '.json', ($details | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))

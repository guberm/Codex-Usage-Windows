param([Parameter(Mandatory)][string]$Exe, [string]$OutputDirectory = "$PSScriptRoot/../artifacts/smoke")
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $Exe)) { throw "Windows client has not been built: $Exe" }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputPath = (Resolve-Path -LiteralPath $OutputDirectory).Path
$process = Start-Process -FilePath $Exe -ArgumentList ('--smoke-test "' + $outputPath + '"') -PassThru
if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'UI smoke test timed out' }
if ($process.ExitCode -ne 0) { throw "UI smoke test failed: $($process.ExitCode)" }
foreach ($name in @('compact.png', 'expanded.png', 'light.png', 'smoke-result.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $outputPath $name))) { throw "Missing smoke artifact: $name" }
}
Get-Content -LiteralPath (Join-Path $outputPath 'smoke-result.txt')

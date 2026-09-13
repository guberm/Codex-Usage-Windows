param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet run --project tests/Checks.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Checks failed' }
$publish = Join-Path $PSScriptRoot 'artifacts/publish'
dotnet publish src/CodexUsage.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:EnableCompressionInSingleFile=true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
& "$PSScriptRoot/tests/Smoke.ps1" -Exe (Join-Path $publish 'CodexUsage.exe')
$release = Join-Path $PSScriptRoot 'artifacts/release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$name = "Codex-Usage-Windows-v$Version-win-x64.exe"
Copy-Item -LiteralPath (Join-Path $publish 'CodexUsage.exe') -Destination (Join-Path $release $name) -Force
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $release $name)).Hash.ToLowerInvariant()
"$hash  $name" | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Get-Item -LiteralPath (Join-Path $release $name), (Join-Path $release 'SHA256SUMS.txt') | Select-Object Name, Length

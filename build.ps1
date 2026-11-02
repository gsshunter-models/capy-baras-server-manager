param([switch]$Test)
$ErrorActionPreference = 'Stop'
if ($Test) {
    & dotnet run --project (Join-Path $PSScriptRoot 'Tests\CapyServer.Tests.csproj') -c Release -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) { throw 'Server manager tests failed.' }
    & dotnet run --project (Join-Path $PSScriptRoot 'Tests.Windows\CapyServer.WindowsTests.csproj') -c Release -p:BuildInParallel=false
    if ($LASTEXITCODE -ne 0) { throw 'Windows credential tests failed.' }
}
$taskOutput = Join-Path $PSScriptRoot 'artifacts\CapyBaraServerManager'
& dotnet publish (Join-Path $PSScriptRoot 'Desktop\CapyServer.Desktop.csproj') -c Release --no-self-contained -o $taskOutput -m:1
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
Write-Output (Join-Path $taskOutput 'CapyBaraServerManager.exe')

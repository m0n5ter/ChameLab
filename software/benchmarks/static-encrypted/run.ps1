param(
    [ValidateRange(1, 1000)][int]$Iterations = 7,
    [ValidateRange(1, 100)][int]$Warmups = 2
)
$ErrorActionPreference = 'Stop'
& dotnet build "$PSScriptRoot/StaticEncryptedBench.csproj" -c Release --nologo
if ($LASTEXITCODE) { throw 'Compilation failed.' }
$previousTiering = $env:DOTNET_TieredCompilation
$previousReadyToRun = $env:DOTNET_ReadyToRun
Push-Location $PSScriptRoot
try {
    $env:DOTNET_TieredCompilation = '0'
    $env:DOTNET_ReadyToRun = '0'
    & dotnet "$PSScriptRoot/bin/Release/net10.0/StaticEncryptedBench.dll" $Iterations $Warmups
    if ($LASTEXITCODE) { throw 'Benchmark or correctness validation failed.' }
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
    $env:DOTNET_ReadyToRun = $previousReadyToRun
    Pop-Location
}

param(
    [ValidateRange(1, 1000)][int]$Iterations = 7,
    [ValidateRange(1, 100)][int]$Warmups = 2,
    [string]$Modes = 'C/1,C/4,C#/1,C#/4,C#/all'
)
$ErrorActionPreference = 'Stop'
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'MSVC x64 C tools are required.' }
$vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
$source = (Resolve-Path "$PSScriptRoot/../../src").Path
$nativeDir = New-Item -ItemType Directory -Force "$PSScriptRoot/bin/native"
Push-Location $nativeDir.FullName
try {
    $files = @("$PSScriptRoot/native.c", "$source/nested_util.c", "$source/crapto1.c", "$source/crypto1.c", "$source/bucketsort.c", "$source/parity.c")
    $quotedFiles = ($files | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $command = '@call "' + $vcvars + '" >nul' + "`r`n" + '@if errorlevel 1 exit /b 1' + "`r`n" + '@cl /nologo /O2 /std:c11 /LD /DWIN32 /D_CRT_SECURE_NO_WARNINGS /I"' + $PSScriptRoot + '" /I"' + $source + '" ' + $quotedFiles + ' /Fe:nested_bench.dll'
    $buildFile = Join-Path $nativeDir.FullName 'build.bat'
    Set-Content -LiteralPath $buildFile -Value $command -Encoding ascii
    & cmd.exe /d /c $buildFile
    if ($LASTEXITCODE) { throw 'Native compilation failed.' }
} finally { Pop-Location }
& dotnet build "$PSScriptRoot/NestedBench.csproj" -c Release --nologo
if ($LASTEXITCODE) { throw 'Managed compilation failed.' }
Copy-Item -LiteralPath "$PSScriptRoot/bin/native/nested_bench.dll" -Destination "$PSScriptRoot/bin/Release/net10.0/nested_bench.dll"
$previousTiering = $env:DOTNET_TieredCompilation
$previousReadyToRun = $env:DOTNET_ReadyToRun
Push-Location $PSScriptRoot
try {
    $env:DOTNET_TieredCompilation = '0'
    $env:DOTNET_ReadyToRun = '0'
    & dotnet "$PSScriptRoot/bin/Release/net10.0/NestedBench.dll" $Iterations $Warmups '../../chamelab/ChameLab.Tests/Reference/crypto_vectors.json' $Modes
    if ($LASTEXITCODE) { throw 'Benchmark or correctness validation failed.' }
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
    $env:DOTNET_ReadyToRun = $previousReadyToRun
    Pop-Location
}

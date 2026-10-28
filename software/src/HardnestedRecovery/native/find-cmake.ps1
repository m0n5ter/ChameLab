$ErrorActionPreference = 'Stop'
$command = Get-Command cmake -CommandType Application -ErrorAction SilentlyContinue
if ($command) {
    Write-Output $command.Source
    exit 0
}
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $locator) {
    $bundled = & $locator -latest -products '*' -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' | Select-Object -First 1
    if ($bundled -and (Test-Path -LiteralPath $bundled)) {
        Write-Output $bundled
        exit 0
    }
}
throw 'CMake 3.24+ is required. Install CMake or pass -p:CMakeExecutable=PATH_TO_CMAKE.'

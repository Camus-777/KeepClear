param([string]$Python = 'python.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
& $Python -c "import sys; assert sys.version_info[:2] == (3,12) and sys.maxsize > 2**32, 'Use Windows x64 Python 3.12'"
if ($LASTEXITCODE -ne 0) { throw 'Windows x64 Python 3.12 is required for the locked wheels.' }
$modelPython = Join-Path $projectRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $modelPython)) {
    & $Python -m venv (Join-Path $projectRoot '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated model environment.' }
}
& $modelPython -m pip install --only-binary=:all: --require-hashes -r (Join-Path $projectRoot 'requirements-models.lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Model dependency installation failed.' }
& $modelPython (Join-Path $projectRoot 'tools\prepare_models.py')
if ($LASTEXITCODE -ne 0) { throw 'Model preparation or checksum verification failed.' }
& (Join-Path $projectRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Write-Host 'Ready: dist\KeepClear.exe (Alkove -> Denoiser). Runtime inference is offline.'

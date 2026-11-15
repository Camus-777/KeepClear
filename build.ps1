param([switch]$Test,[string]$OutputDirectory='')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.8 Windows compiler is required.' }
$dist = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'dist' }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$refs = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Xml.dll','/r:System.IO.Compression.dll')
$driverZip = Join-Path $projectRoot 'drivers\VBCABLE_Driver_Pack46.zip'
if ((Get-FileHash -LiteralPath $driverZip -Algorithm SHA256).Hash -ne 'bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90') { throw 'Original VB-CABLE archive checksum mismatch.' }
$resources = @("/resource:$driverZip,KeepClear.VBCable.zip")
& (Join-Path $projectRoot 'assets\build-icon.ps1')
& $compiler /nologo /target:winexe /platform:x64 /unsafe+ /optimize+ /warn:4 "/win32icon:$projectRoot\assets\KeepClear.ico" "/win32manifest:$projectRoot\app.manifest" "/out:$dist\KeepClear.exe" @refs @resources @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
New-Item -ItemType Directory -Force -Path (Join-Path $dist 'workers') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'workers\model_worker.py') -Destination (Join-Path $dist 'workers') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'third_party') -Destination $dist -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $dist 'models') | Out-Null
if (Test-Path -LiteralPath (Join-Path $projectRoot 'models\MANIFEST.json')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'models') -Destination $dist -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $dist -Recurse -Force
foreach ($publicationFile in @('README.md','README.zh-CN.md','THIRD-PARTY.md','LICENSE','NOTICE','LICENSING.md','CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $publicationFile) -Destination $dist -Force }
Set-Content -LiteralPath (Join-Path $dist 'KeepClear.exe.config') -Encoding UTF8 -Value '<?xml version="1.0"?><configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup></configuration>'
if ($Test) {
    $testSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object FullName)
    & $compiler /nologo /target:exe /platform:x64 /unsafe+ /optimize+ "/win32icon:$projectRoot\assets\KeepClear.ico" "/win32manifest:$projectRoot\app.manifest" "/out:$dist\KeepClear.Tests.exe" @refs @resources @sources @testSources /main:KeepClear.Tests
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $dist 'KeepClear.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & python -m unittest discover -s (Join-Path $projectRoot 'tests') -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Worker tests failed.' }
}
Write-Host "Built: $dist\KeepClear.exe"

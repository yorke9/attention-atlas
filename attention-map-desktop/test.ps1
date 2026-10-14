param([Alias('ExecutablePath')][string]$ExePath)
$ErrorActionPreference = 'Stop'
$atlasExe = Join-Path (Split-Path $PSScriptRoot -Parent) '注意力地图-Windows版.exe'
if ($ExePath) { $atlasExe = [System.IO.Path]::GetFullPath($ExePath) }
$atlasTestPath = Join-Path $PSScriptRoot '.local\verification'
New-Item -ItemType Directory -Path $atlasTestPath -Force | Out-Null
$atlasTestProcess = Start-Process -FilePath $atlasExe -ArgumentList @('--self-test', ('"' + $atlasTestPath + '"')) -WindowStyle Hidden -PassThru
if (-not $atlasTestProcess.WaitForExit(30000)) { throw '测试尚未结束，请检查 .local\verification\self-test.json。' }
$atlasReport = Join-Path $atlasTestPath 'self-test.json'
if (-not (Test-Path -LiteralPath $atlasReport)) { throw '测试未生成报告。' }
Get-Content -LiteralPath $atlasReport -Encoding UTF8
if ($atlasTestProcess.ExitCode -ne 0) { throw '测试失败，请查看上面的报告。' }

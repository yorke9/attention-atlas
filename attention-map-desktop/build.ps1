param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$atlasSource = Join-Path $PSScriptRoot 'AttentionAtlas.cs'
$atlasManifest = Join-Path $PSScriptRoot 'app.manifest'
$atlasOutput = Join-Path (Split-Path $PSScriptRoot -Parent) '注意力地图-Windows版.exe'
if ($OutputPath) { $atlasOutput = [System.IO.Path]::GetFullPath($OutputPath) }
$atlasCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $atlasCompiler)) { throw '缺少 Windows .NET Framework C# 编译器。' }
& $atlasCompiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/win32manifest:$atlasManifest" "/out:$atlasOutput" $atlasSource
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
$atlasIcon = Join-Path $PSScriptRoot 'attention-atlas.ico'
$atlasIconProcess = Start-Process -FilePath $atlasOutput -ArgumentList @('--make-icon', ('"' + $atlasIcon + '"')) -WindowStyle Hidden -Wait -PassThru
if ($atlasIconProcess.ExitCode -ne 0) { throw '图标生成失败。' }
& $atlasCompiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/win32manifest:$atlasManifest" "/win32icon:$atlasIcon" "/out:$atlasOutput" $atlasSource
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output $atlasOutput

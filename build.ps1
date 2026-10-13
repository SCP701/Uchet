param([switch]$Test)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path -LiteralPath $compiler)) { throw 'Не найден компилятор .NET Framework.' }
$outputDir = Join-Path $projectDir 'dist'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$references = @('/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','/r:System.Xml.Linq.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll')
$iconPath = Join-Path $projectDir 'resources\AppIcon.ico'
if (!(Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw 'Не найдена иконка приложения resources\AppIcon.ico.' }
$common = @('/nologo','/platform:anycpu','/optimize+',('/win32manifest:'+(Join-Path $projectDir 'app.manifest')),('/win32icon:'+$iconPath))
$sourceFiles = @(Get-ChildItem -LiteralPath $projectDir -Filter '*.cs' | ForEach-Object { $_.FullName })
$outputExe = Join-Path $outputDir 'AktiOplaty.exe'
& $compiler @common @references ('/resource:'+(Join-Path $projectDir 'resources\ActTemplate.docx')+',WorkLedger.ActTemplate.docx') ('/resource:'+$iconPath+',WorkLedger.AppIcon.ico') /target:winexe ('/out:'+$outputExe) @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Сборка приложения не удалась.' }
$helperSources = @('UpdateProtocol.cs','TrustedUpdateKey.cs','Core.cs','Reports.cs','ActDocument.cs','ActHtmlDocument.cs','AssemblyInfo.cs','updater\UpdaterProgram.cs') | ForEach-Object { Join-Path $projectDir $_ }
& $compiler @common @references /target:winexe ('/out:'+(Join-Path $outputDir 'Updater.exe')) @helperSources
if ($LASTEXITCODE -ne 0) { throw 'Сборка модуля обновления не удалась.' }
$releaseSources = @('UpdateProtocol.cs','TrustedUpdateKey.cs','maintenance\ReleaseTool.cs') | ForEach-Object { Join-Path $projectDir $_ }
& $compiler @references /nologo /target:exe ('/out:'+(Join-Path $outputDir 'ReleaseTool.exe')) @releaseSources
if ($LASTEXITCODE -ne 0) { throw 'Сборка инструмента выпуска не удалась.' }
if ($Test) {
    $testDir = Join-Path $projectDir '.verification'
    $processArgs = @('--self-test','--data-dir',('"'+$testDir+'"'))
    $testProcess = Start-Process -FilePath $outputExe -ArgumentList $processArgs -WindowStyle Hidden -PassThru -Wait
    if ($testProcess.ExitCode -ne 0) { throw ('Проверки не пройдены: '+(Join-Path $testDir 'error.txt')) }
    Get-Content -LiteralPath (Join-Path $testDir 'test-result.txt')
    $helperHarness = Join-Path $testDir 'UpdaterTestHarness.cs'
    $harnessBody = 'using System; namespace WorkLedger { internal static class UpdaterTestHarness { [STAThread] private static int Main(string[] args) { try { UpdaterTests.Run(args[0]); return 0; } catch(Exception e) { Console.Error.WriteLine(e); return 1; } } } }'
    [IO.File]::WriteAllText($helperHarness,$harnessBody,[Text.UTF8Encoding]::new($true))
    $helperTestExe = Join-Path $testDir 'UpdaterTests.exe'
    $helperTestSources = @($helperSources) + @(Join-Path $projectDir 'updater\UpdaterTests.cs') + @($helperHarness)
    & $compiler @common @references /r:Microsoft.CSharp.dll /target:exe /main:WorkLedger.UpdaterTestHarness ('/out:'+$helperTestExe) @helperTestSources
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать проверки обновлятора.' }
    $helperTestRoot = Join-Path $projectDir '.uverify'
    $helperTestLog = Join-Path $testDir 'updater-test-log.txt'
    $helperTestErrors = Join-Path $testDir 'updater-test-errors.txt'
    $helperTestProcess = Start-Process -FilePath $helperTestExe -ArgumentList @('"'+$helperTestRoot+'"') -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $helperTestLog -RedirectStandardError $helperTestErrors
    Get-Content -LiteralPath $helperTestLog
    if ($helperTestProcess.ExitCode -ne 0) { Get-Content -LiteralPath $helperTestErrors; throw 'Проверки обновлятора не прошли.' }

}
Write-Output $outputExe




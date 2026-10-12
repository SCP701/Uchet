param(
    [string]$SigningKeyPath = '',
    [string]$OutputPath = '',
    [string]$Repository = '',
    [switch]$SkipBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$releaseProjectDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SigningKeyPath)) {
    $SigningKeyPath = Join-Path $releaseProjectDir '.release-signing-key.xml'
}
$SigningKeyPath = [IO.Path]::GetFullPath($SigningKeyPath)
if (!(Test-Path -LiteralPath $SigningKeyPath -PathType Leaf)) {
    throw 'Не найден первоначальный приватный ключ подписи. Восстановите его из сохранённой копии; не создавайте новый ключ для уже установленного приложения.'
}
if (![string]::IsNullOrWhiteSpace($Repository) -and $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Укажите репозиторий в формате owner/repo.'
}
if (!$SkipBuild) {
    & (Join-Path $releaseProjectDir 'build.ps1') -Test
}
$releaseDistDir = Join-Path $releaseProjectDir 'dist'
$releaseToolPath = Join-Path $releaseDistDir 'ReleaseTool.exe'
$releaseMainPath = Join-Path $releaseDistDir 'AktiOplaty.exe'
if (!(Test-Path -LiteralPath $releaseToolPath -PathType Leaf) -or !(Test-Path -LiteralPath $releaseMainPath -PathType Leaf)) {
    throw 'Не найдена готовая сборка и ReleaseTool.exe. Выполните build.ps1 или запустите подготовку без -SkipBuild.'
}
$releaseVersion = [Reflection.AssemblyName]::GetAssemblyName($releaseMainPath).Version
$releaseVersionLabel = if ($releaseVersion.Revision -eq 0) { $releaseVersion.ToString(3) } else { $releaseVersion.ToString(4) }
$releaseTag = 'v' + $releaseVersionLabel
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $releaseDistDir ('releases\' + $releaseTag)
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
& $releaseToolPath '--input' $releaseDistDir '--output' $OutputPath '--key' $SigningKeyPath
if ($LASTEXITCODE -ne 0) { throw 'Подготовка подписанного выпуска не пройдена. Публикация не выполнялась.' }
$releaseFiles = @(
    (Join-Path $OutputPath 'AktiOplaty-update.zip'),
    (Join-Path $OutputPath 'update-manifest.json'),
    (Join-Path $OutputPath 'update-manifest.sig')
)
foreach ($releaseFile in $releaseFiles) {
    if (!(Test-Path -LiteralPath $releaseFile -PathType Leaf)) { throw 'Не создан обязательный файл выпуска.' }
}
Write-Output ('Папка проверенного выпуска: ' + $OutputPath)
if (![string]::IsNullOrWhiteSpace($Repository)) {
    function ConvertTo-ReleaseLiteral([string]$Value) { return "'" + $Value.Replace("'", "''") + "'" }
    $releaseCommand = 'gh release create ' + (ConvertTo-ReleaseLiteral $releaseTag)
    foreach ($releaseFile in $releaseFiles) { $releaseCommand += ' ' + (ConvertTo-ReleaseLiteral $releaseFile) }
    $releaseCommand += ' --repo ' + (ConvertTo-ReleaseLiteral $Repository)
    $releaseCommand += ' --title ' + (ConvertTo-ReleaseLiteral ('Акты и оплаты ' + $releaseVersionLabel))
    $releaseCommand += ' --notes ' + (ConvertTo-ReleaseLiteral ('Подписанное обновление для Windows. Версия ' + $releaseVersionLabel + '.'))
    Write-Output 'Публикация не выполнялась. После проверки выпуска можно выполнить следующую команду:'
    Write-Output $releaseCommand
} else {
    Write-Output 'Публикация не выполнялась. Для готовой команды публикации укажите -Repository owner/repo.'
}
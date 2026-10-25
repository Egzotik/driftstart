param(
    [Parameter(Mandatory=$true)][string]$FilePath,
    [Parameter(Mandatory=$true)][string]$CertificateThumbprint,
    [string]$SignToolPath,
    [string]$TimestampUrl = 'https://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$taskTarget = (Resolve-Path -LiteralPath $FilePath).Path
if ([System.IO.Path]::GetExtension($taskTarget) -ne '.exe') { throw 'Укажите EXE, полученный после финальной сборки.' }
$taskThumbprint = $CertificateThumbprint.Replace(' ', '')
$taskCertificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $taskThumbprint)
if (-not $taskCertificate.HasPrivateKey -or $taskCertificate.NotAfter -le (Get-Date)) { throw 'Нужен действующий сертификат с доступным закрытым ключом / токеном.' }
if (-not ($taskCertificate.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')) { throw 'Сертификат не предназначен для подписи кода.' }
if (-not $SignToolPath) {
    $taskSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $SignToolPath = Get-ChildItem -LiteralPath $taskSdk -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object FullName -Match '\\x64\\signtool.exe$' |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath)) { throw 'Установите Windows SDK Signing Tools или укажите -SignToolPath.' }
& $SignToolPath sign /sha1 $taskThumbprint /s My /fd SHA256 /tr $TimestampUrl /td SHA256 $taskTarget
if ($LASTEXITCODE -ne 0) { throw 'SignTool не подписал EXE. Файл нельзя объявлять подписанным.' }
& $SignToolPath verify /pa /all /v $taskTarget
if ($LASTEXITCODE -ne 0) { throw 'Проверка доверенной Authenticode-подписи не прошла.' }
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskTarget
if ($taskSignature.Status -ne 'Valid') { throw ('Подпись не доверенная: ' + $taskSignature.Status) }
Write-Host 'Подпись Authenticode и timestamp успешно проверены.'
Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256

param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\app'), [string]$CertificateThumbprint, [string]$SignToolPath)
$ErrorActionPreference = 'Stop'
$taskProject = Join-Path $PSScriptRoot '..\desktop\DriftStart.csproj'
dotnet publish $taskProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
$taskExe = Join-Path $OutputDirectory 'DriftStart.exe'
if ($CertificateThumbprint) {
    & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -FilePath $taskExe -CertificateThumbprint $CertificateThumbprint -SignToolPath $SignToolPath
} else {
    Write-Warning 'EXE собран БЕЗ цифровой подписи. Сертификат издателя не задан.'
}
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskExe
$taskVersion = ([xml](Get-Content -LiteralPath $taskProject)).Project.PropertyGroup.Version | Select-Object -First 1
$taskManifest = [ordered]@{
    version = $taskVersion; file = 'DriftStart.exe'; sha256 = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash
    signatureStatus = $taskSignature.Status.ToString(); createdUtc = [DateTime]::UtcNow.ToString('o')
}
$taskManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release.json') -Encoding UTF8


# Подпись и автоматические обновления DriftStart

## Настоящая подпись Windows

Нужен действующий OV/EV Code Signing сертификат либо Azure Trusted Signing. Самоподписанный сертификат не устраняет предупреждения браузера и Microsoft SmartScreen.

После установки сертификата в `Cert:\CurrentUser\My`:

```powershell
.\scripts\Build-Release.ps1 -OutputDirectory .\artifacts\release -CertificateThumbprint "ОТПЕЧАТОК_СЕРТИФИКАТА"
```

Скрипт подписывает `DriftStart.exe`, добавляет timestamp DigiCert, проверяет доверие подписи и создаёт `release.json` с SHA-256. Нельзя публиковать файл, если проверка вернула не `Valid`.

## GitHub Releases

Рекомендуется создать отдельный публичный репозиторий `driftstart`, а не складывать выпуски в исходный проект лаунчера. В каждом Release должны лежать:

- `DriftStart.exe` — подписанный установочный файл;
- `DriftStart.sha256` — SHA-256 и имя файла;
- архив для ручной загрузки при необходимости.

Постоянные ссылки будут выглядеть так:

```text
https://github.com/OWNER/driftstart/releases/latest/download/DriftStart.exe
https://github.com/OWNER/driftstart/releases/latest/download/DriftStart.sha256
```

В Cloudflare Worker задаются переменные:

```text
UPDATE_VERSION=1.3.0
UPDATE_URL=https://github.com/OWNER/driftstart/releases/latest/download/DriftStart.exe
UPDATE_SHA256_URL=https://github.com/OWNER/driftstart/releases/latest/download/DriftStart.sha256
UPDATE_NOTES=Описание обновления
```

После этого уже установленные версии увидят новую версию при запуске, проверят SHA-256, заменят свой EXE и запустятся снова.

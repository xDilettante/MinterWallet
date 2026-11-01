## Скачать

- **Windows 10/11 (x64):** `MinterWallet-{version}-win-x64.exe`. Установка не нужна — просто запустите файл. Если
  Windows покажет «Система Windows защитила ваш компьютер», нажмите «Подробнее» → «Выполнить в любом случае».
- **Linux (x64, с графическим рабочим столом)** — любой из вариантов:
  - `MinterWallet-{version}-x86_64.AppImage` — запускается на большинстве дистрибутивов:
    `chmod +x MinterWallet-{version}-x86_64.AppImage && ./MinterWallet-{version}-x86_64.AppImage`;
  - `minter-wallet_{version}_amd64.deb` — для Debian, Ubuntu, Mint: `sudo apt install ./minter-wallet_{version}_amd64.deb`,
    затем «Minter Wallet» в меню приложений;
  - `MinterWallet-{version}-linux-x64` — просто файл: `chmod +x` и запустить.
- **Android 9+ (arm64):** `MinterWallet-{version}-android-arm64.apk` — скачайте и откройте файл на телефоне (при первом запуске разрешите установку из браузера или файлового менеджера).
  Этот же файл используется для обновления: установите поверх прежней версии, без удаления приложения и его данных.

Устанавливать .NET не нужно: всё нужное уже внутри. Проверить, что файл скачался без искажений:
`sha256sum -c SHA256SUMS.txt --ignore-missing` в Linux или `Get-FileHash <файл>` в PowerShell.

## Важно

- Ключи хранятся только на вашем устройстве, зашифрованные паролем: `%LOCALAPPDATA%\MinterWallet` в Windows,
  `~/.local/share/MinterWallet` в Linux, внутренняя папка приложения в Android.
- Сохраните фразу восстановления: пароль не восстанавливается, а по фразе кошелёк можно восстановить на любом
  устройстве.
- На Android PIN привязан к исходной установке. Для восстановления на другом устройстве нужна фраза или приватный ключ.
- По умолчанию кошелёк подключается к MinterGate `https://188.127.237.145:8851`; в настройках можно выбрать свою
  ноду Minter или другой адрес. Если источник недоступен, кошелёк назовёт причину; что с ней делать — в
  [README](https://github.com/xDilettante/MinterWallet#если-источник-недоступен).

Собрано из этого тега на GitHub Actions: `.github/workflows/release.yml`.

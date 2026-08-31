# Happ Accessible

Доступный Windows-клиент VPN/прокси для **NVDA** (.NET 8 WPF + трей).

## Возможности (0.3.36)

- Подписки / импорт: `vless`, `vmess`, `trojan`, `ss`, `hysteria` / `hysteria2`, `wireguard`, Clash YAML, **Xray/sing-box JSON**, ссылки **INCY** (`incy://crypt1/`, `incy://add/`, `incy://import/`)
- **AmneziaWG / WireGuard** — импорт `.conf`
- **Dual-core:** [sing-box-lx](https://github.com/Leadaxe/sing-box-lx) (TUN, маршруты, **xhttp**, hy2/WG) и Xray (Reality/Vision в режиме Авто)
- Одна кнопка **Подключить / Отключить**, пинг и **диагностика** выбранного сервера (TCP, туннель, DNS)
- Автовосстановление сессии: полная проверка (mixed-порт, HTTP, HTTPS, DNS), профилактический перезапуск туннеля каждые 90 мин, мониторинг AmneziaWG, безопасный refresh с cooldown
- **Kill switch** (TUN, от администратора) — блокирует прямой трафик при обрыве VPN
- **Избранные серверы** — фильтр и сортировка по последнему успешному подключению
- **Журнал сессии** — последние 20 событий (Справка → Журнал сессии)
- **Тихое обновление приложения** — без окна установщика; portable обновляется на месте; ручная проверка спрашивает подтверждение
- Безопасный системный прокси: маркер сессии, сохранение сессии до записи в реестр, восстановление после сбоя
- Секреты подписки/Remnawave в DPAPI; стабильный HWID для панелей
- Сохранение последнего рабочего списка серверов при ошибке подписки
- Один экземпляр приложения (повторный запуск активирует окно)
- **DNS-профили Happ / INCY** — импорт `happ://routing/add/…` и `incy://routing/add/…` (DoU/DoH, FakeDNS, DnsHosts)
- Транзакционное обновление ядер sing-box/Xray (backup + откат)
- Информация о подписке: трафик, срок, время обновления
- Переименование серверов (F2), сохраняется локально
- Меню трея: серверы, проверка соединения, пинг
- Маршруты: списки доменов, `geosite:` / `geoip:` (rule-set с GitHub), режимы по приложениям
- **Автообновление ядер** (sing-box-lx, Xray, AmneziaWG) и **приложения** через GitHub Releases
- **Совместимость INCY** — Справка → Обновить совместимость INCY (User-Agent и ключи crypt с GitHub)
- Кнопка **Логи** / Справка → Открыть логи — `app.log` с ошибками
- Системный прокси и TUN, автопереключение на обход белых списков

## Happ crypt (`happ://crypt…`)

Расшифровка не поддерживается (ключ только в официальном Happ).  
Варианты: открытая подписка, вставка расшифрованного списка, или [запрос a11y](https://issues.happ.su/).

## INCY (`incy://…`)

`incy://crypt1/…` расшифровывается локально по [открытому пакету](https://github.com/INCY-DEV/incy-link-encoder) и сохраняется как обычный URL подписки. Также принимаются `incy://add/`, `incy://import/` и профили `incy://routing/…`. Ссылки `incy://connect` / `disconnect` не управляют туннелем — используйте кнопки этого клиента.

Ключи и User-Agent подтягиваются с GitHub (**Справка → Обновить совместимость INCY**), встроенный crypt1 не удаляется.

Это не официальный INCY: нет Send to TV, Premium-панели и Lite Mode. Ядра Xray/sing-box у нас свои, из INCY ничего не копируется. Запрос доступности самого INCY на Windows: [feedback.incy.cc](https://feedback.incy.cc).

### Если обновился INCY

В клиенте нет «модулей INCY» в виде dll/ядра. Подтягивать нужно только формат ссылок и заголовки. Источники правды:

| Что сломалось | Куда смотреть | Что править |
|---|---|---|
| `incy://crypt1/` больше не открывается, в ссылках появился `crypt2/` | [incy-link-encoder](https://github.com/INCY-DEV/incy-link-encoder) (`src/core.ts`, `assets/*.bin`, тест `go/incylink_test.go`) | `IncyCryptCodec.cs`: соль (`incy`+`deep`+`crypt1`+`v2026.06`), срезы keymat (offset 1024 и 2048), `KeyFingerprint`. **crypt1 оставить**, рядом добавить схему crypt2 |
| Панель пишет «App not supported» / пустая подписка | [Releases](https://github.com/INCY-DEV/incy-platforms/releases) и [заголовки клиента](https://docs.incy.cc/en/subscription-format/) | `SubscriptionFetcher.cs`: константа `IncyCompatibilityUserAgent` (`INCY/3.7.2`) |
| Новая ссылка (`incy://что-то/`) | [Deep Links](https://docs.incy.cc/en/deep-links/) | `IncyDeepLink.cs` |
| Профиль маршрутизации не импортируется | [Routing](https://docs.incy.cc/routing/) | `HappRoutingImporter` в `HappRoutingProfileStore.cs` |
| Нет трафика/имени/роутинга из подписки | [HTTP-заголовки](https://docs.incy.cc/en/subscription-format/) | `SubscriptionFetcher.ApplyUserInfo` |

Проверка crypt после смены ключа:

1. Взять `KEY_FINGERPRINT` и pinned vector из encoder.
2. Срезы 32 байт из `incy_assets_a.bin` (offset 1024) и `incy_assets_b.bin` (offset 2048).
3. `K1 = SHA256("incy" + "deep" + "cryptN" + "vYYYY.MM" + sliceA + sliceB)`, отпечаток = `SHA256(K1)` в hex.
4. Debug-сборка при первом обращении к codec проверяет pinned vector; если вектор не сходится — тип не загрузится.

User-Agent менять только если панели INCY начинают отклонять старый. Удачный UA запоминается (`LastSuccessfulUserAgent`), поэтому достаточно **добавить** новый в список, не выкидывая `Happ/3.3.6`.

## Скачать

**[Releases](https://github.com/Nikita34196/happ-accessible/releases)** — Setup.exe и portable zip.

## Сборка локально

Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) и Windows.

```powershell
dotnet publish HappAccessible\HappAccessible.csproj -c Release -r win-x64 --self-contained true -o publish\app
.\publish\app\HappAccessible.exe
```

Установщик + портатив (нужен [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release-artifacts.ps1
```

## CI и релизы на GitHub

| Workflow | Когда | Что делает |
|---|---|---|
| **Build** | push / PR в `main` | сборка + артефакт win-x64 |
| **Release** | тег `v0.3.36` (или ручной запуск) | portable zip + Setup.exe → GitHub Release |

```powershell
git tag v0.3.36
git push origin v0.3.36
```

## Клавиши

| Клавиша | Действие |
|---|---|
| Alt, П | Меню подписки |
| F5 | Обновить подписку |
| Ctrl+Shift+C / Ctrl+Shift+D | Подключить или отключить |
| F2 | Переименовать сервер |
| Alt+H / Alt+G | Пинг выбранного / всех |
| Alt+D | Диагностика выбранного сервера |
| Alt+B | Проверить обход белых списков |
| Alt+S | Сохранить |
| Enter в списке | Подключить / отключить |

При закрытии окна (если включено «Сворачивать в трей») приложение уходит в трей; полный выход — пункт **Выход** в меню значка.

## License

MIT — см. [LICENSE](LICENSE).

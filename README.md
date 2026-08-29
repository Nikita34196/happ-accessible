# Happ Accessible

Доступный Windows-клиент VPN/прокси для **NVDA** (.NET 8 WPF + трей).

## Возможности (0.3.21)

- Подписки / импорт: `vless`, `vmess`, `trojan`, `ss`, `hysteria` / `hysteria2`, `wireguard`, Clash YAML, **Xray/sing-box JSON**
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
- DNS через DoH с резервным сервером, ротация DNS-кэша sing-box; подсказка про Secure DNS в Chrome
- Транзакционное обновление ядер sing-box/Xray (backup + откат)
- Информация о подписке: трафик, срок, время обновления
- Переименование серверов (F2), сохраняется локально
- Меню трея: серверы, проверка соединения, пинг
- Маршруты: списки доменов, `geosite:` / `geoip:` (rule-set с GitHub), режимы по приложениям
- **Автообновление ядер** (sing-box-lx, Xray, AmneziaWG) и **приложения** через GitHub Releases
- Кнопка **Логи** / Справка → Открыть логи — `app.log` с ошибками
- Системный прокси и TUN, автопереключение на обход белых списков

## Happ crypt (`happ://crypt…`)

Расшифровка не поддерживается (ключ только в официальном Happ).  
Варианты: открытая подписка, вставка расшифрованного списка, или [запрос a11y](https://issues.happ.su/).

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
| **Release** | тег `v0.3.21` (или ручной запуск) | portable zip + Setup.exe → GitHub Release |

```powershell
git tag v0.3.21
git push origin v0.3.21
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

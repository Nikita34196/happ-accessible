# Happ Accessible

Доступный Windows-клиент VPN/прокси для **NVDA** (.NET 8 WPF + трей).

## Возможности (0.3.6)

- Открытые подписки / вставка списка: `vless`, `vmess`, `trojan`, `ss`, `hysteria2`/`hy2`
- **AmneziaWG / WireGuard** — импорт `.conf`
- **Dual-core:** **sing-box** (TUN, маршруты, hy2) и **Xray** (прокси Reality/Vision в режиме Авто)
- **Меню трея:** выбор сервера и подключение прямо из значка (ПКМ → Серверы)
- Проверка обновлений ядер при старте через GitHub releases (sing-box, Xray, AmneziaWG)
- **Системный прокси** и **TUN** (авто-UAC; стек TUN: gvisor / mixed / system)
- Настраиваемый **mixed-порт** (по умолчанию 2080)
- Меню **Alt → Подписка**, режимы по сайтам / РФ / приложениям
- Проверка серверов обхода белых списков
- Автообновление подписки и автопереключение на обход БС
- Пинг, трей, сохранение настроек, автоподключение

## Happ crypt (`happ://crypt…`)

Расшифровка не поддерживается (ключ только в официальном Happ).  
Варианты: открытая подписка, вставка расшифрованного списка, или [запрос a11y](https://issues.happ.su/).

## Скачать

После публикации релизов: **GitHub → Releases** (Setup.exe + portable zip).

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
| **Release** | тег `v0.3.6` (или ручной запуск) | portable zip + Setup.exe → GitHub Release |

Опубликовать релиз:

```powershell
git tag v0.3.6
git push origin v0.3.6
```

## Клавиши

| Клавиша | Действие |
|---|---|
| Alt, П | Меню подписки |
| F5 | Обновить подписку |
| Ctrl+Shift+C | Подключить |
| Ctrl+Shift+D | Отключить |
| Alt+G | Пинг |
| Alt+B | Проверить обход белых списков |
| Alt+S | Сохранить |
| Alt+A / Alt+M | Автоподключение / трей |
| Alt+U / Alt+W | Автообновление / обход БС |
| Alt+P / Alt+T | Прокси / TUN |
| Enter в списке | Подключить |

При закрытии окна (если включено «Сворачивать в трей») приложение уходит в трей; полный выход — пункт **Выход** в меню значка.

## License

MIT — см. [LICENSE](LICENSE).

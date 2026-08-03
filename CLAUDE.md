# SpaceRadarBot — Memory Bank

Telegram-бот для отслеживания космических запусков: показывает ближайшие старты,
шлёт уведомления за ~30 минут до запуска и о переносах, переводит описания на русский через OpenAI.
Аудитория маленькая (единицы пользователей), интерфейс на русском, код и коммиты — на английском.

## Команды разработки

```bash
dotnet build SpaceRadarBot/SpaceRadarBot.csproj        # сборка
dotnet test SpaceRadarBot.Tests/SpaceRadarBot.Tests.csproj   # тесты (xunit, чистая логика)
dotnet publish SpaceRadarBot/SpaceRadarBot.csproj -c Release -o ./publish   # прод-сборка (framework-dependent)
```

⚠️ **Не запускать бота локально с прод-конфигом**: локальный `appsettings.json` содержит боевой токен,
и запуск начнёт поллинг того же бота, что крутится на VPS (конфликт getUpdates).

## Структура

```
SpaceRadarBot/               # основной проект (top-level Program.cs)
├── Program.cs               # композиция: конфиг, GetMe (валидация токена), старт сервисов, поллинг
├── Handlers/BotHandlers.cs  # все команды и callback-кнопки; только private-чаты
├── Services/
│   ├── LaunchSyncService.cs        # таймер: тянет Launch Library API → UpsertLaunches
│   ├── NotificationService.cs      # таймер (1 мин): переносы, due-уведомления, автоподписки
│   ├── TranslationService.cs       # OpenAI chat completions, EN→RU
│   ├── MessageFormatter.cs         # ЕДИНСТВЕННОЕ место форматирования сообщений (ru-RU culture)
│   ├── SpectacleRatingCalculator.cs # эвристика 1-5⭐ (пилотируемые/дальний космос/тип ракеты)
│   └── LaunchService.cs            # тонкая async-обёртка над DatabaseService
├── Data/DatabaseService.cs  # ВСЯ работа с LiteDB; singleton-инстанс, write-lock
└── Models/                  # Launch (BsonId=string id из API), Subscription, UserPreference,
                             # UserBlacklist, PostponementNotification, DTO Launch Library
SpaceRadarBot.Tests/         # xunit: форматтер, матчинг предпочтений, рейтинг
tmp-find/                    # локальный инструмент инспекции LiteDB-снимков (gitignored)
```

## Ключевые инварианты (не ломать)

- **Один `LiteDatabase` на процесс** (direct-режим держит файл эксклюзивно). `DatabaseService` —
  singleton с `IDisposable`; check-then-insert операции под `_writeLock`. НЕ возвращаться
  к `using var db = new LiteDatabase(...)` per-операция — это гонки открытия файла.
- **Таймеры непериодические**: тик перевзводит себя в `finally` (`Timeout.InfiniteTimeSpan` + `Change`).
  Иначе долгий тик накладывается на следующий → дубли уведомлений.
- **Отписка пользователя всегда блэклистит запуск** (`RemoveSubscription`), независимо от типа
  подписки — иначе автоподписка вернёт её через минуту.
- **Все даты в БД — UTC**: `BsonMapper.Global.RegisterType<DateTime>` в Program.cs сериализует
  в UTC и восстанавливает Kind=Utc. `TimezoneOffset` пользователя — целые часы, применяется
  только при отображении.
- **Legacy Markdown** (не V2): `MessageFormatter.SanitizeMd` вычищает `_ * [ \`` из данных API.
  Все user-facing тексты — только через `MessageFormatter`.
- **Обрезанный перевод не кэшировать**: `TranslationService` проверяет `finish_reason == "length"`
  и возвращает null — иначе битый текст остался бы в `DescriptionRu` навсегда.

## Данные (LiteDB, `spaceradar.db`)

| Коллекция | Ключевое | Особенности |
|---|---|---|
| `launches` | `_id` = UUID из Launch Library | `ManualRatingOverride` защищает админский рейтинг от синка; `DescriptionRu` сохраняется при апдейтах |
| `userPreferences` | автоинкремент, индекс UserId | Одна строка на пользователя. `CreatedAt` = первое ПИШУЩЕЕ событие (до 2026-06-12 — первая настройка, не первый /start); `LastInteractionAt` пишется с 2026-06-12 на каждый апдейт |
| `subscriptions` | автоинкремент, индексы UserId/LaunchId | `IsAutomatic` различает ручные и автоподписки; `NotificationTime` = LaunchTime − 30 мин |
| `userBlacklist` | — | «не автоподписывать меня на этот запуск»; чистится при `Preference=None` |
| `postponementNotifications` | — | дедуп по (UserId, LaunchId, !Sent): повторный сдвиг обновляет запись |

Чистка: `RemoveOldLaunches(30)` + `CleanupOrphanedData()` каждый синк;
`ClearStalePostponementNotifications(24h)` на старте.

## Внешние API

- **Launch Library 2.3.0** (`ll.thespacedevs.com/2.3.0/launches/upcoming/?mode=detailed`).
  Анонимный лимит **15 req/час с IP** — sync каждые 10 мин × до 2 страниц (limit=50) = 12 req/час.
  `mode=detailed` нужен для бустеров (`rocket.launcher_stage[]`). Для дальних запусков бустеры —
  placeholder'ы (`is_placeholder=true`, serial "Unknown FH", `landing=null`) — UI это учитывает
  (показывает только при непустом SerialNumber). Поиск конкретного запуска: `&search=<name>`.
- **OpenAI chat completions** для переводов; модель из конфига (`OpenAI:Model`), дефолт `gpt-4o-mini`.
  Прод-конфиг может ещё содержать `gpt-3.5-turbo` — при деплое стоит обновить.

## Прод (Google Cloud, free tier)

- Мигрировано 2026-08-03 с DigitalOcean (экономия $7/мес). GCP-проект `spaceradar-bot`,
  VM `spaceradar-vm` (e2-micro, us-central1-a, Ubuntu 24.04, 30 GB standard PD) — конфигурация
  строго в рамках Always Free; бюджет-алерт `free-tier-guard` ($1) на биллинг-аккаунте.
- Внешний IP — эфемерный (меняется при stop/start VM) и намеренно НЕ хранится в репо:
  локально лежит в `deploy.local.json` (gitignored), актуальный всегда виден в консоли GCP.
- Бот: `/home/spaceradar/bot/`, юнит **`telegrambot.service`** (`User=spaceradar`),
  framework-dependent: `/usr/bin/dotnet /home/spaceradar/bot/SpaceRadarBot.dll`.
  Рантаймы из Ubuntu-репозитория: `dotnet-runtime-10.0` + `aspnetcore-runtime-10.0` (обязателен).
- SSH: `ssh spaceradar@<IP из deploy.local.json>` c ключом `~/.ssh/id_ed25519` (Windows-машина разработчика).
- БД на проде: `/home/spaceradar/bot/spaceradar.db`. Инспекция: `cp` на сервере → `scp` вниз →
  `dotnet run --project tmp-find -- <путь>` (LiteDB open ReadOnly).
  ⚠️ Копировать **оба файла**: `spaceradar.db` И `spaceradar-log.db` (WAL) в одну папку —
  основной файл отстаёт на часы (чекпоинт не происходит при systemd stop), свежие записи живут в логе.
- НЕ ставить `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` — сломает ru-RU даты.
- В проде лежат stale `DeepL.net.dll`/`Polly*.dll` от старых сборок, мёртвая секция `DeepL:ApiKey`
  и `OpenAI:Model = gpt-3.5-turbo` (в репо дефолт gpt-4o-mini) — вычистить при следующем деплое.
- Старый DO-дроплет `159.223.223.83` (`/root/bot`, юнит выключен) — держим как бэкап ~неделю
  после миграции, потом удалить в панели DO, чтобы остановить списания.

## Безопасность / git-гигиена

- Секреты только в `appsettings.json` (gitignored). В `appsettings.example.json` — только плейсхолдеры;
  Program.cs распознаёт плейсхолдер `your-openai-api-key-here` и отключает переводы.
- ⚠️ 2026-07-30: реальный OpenAI-ключ засветился в рабочей копии example-файла (в git не попал) —
  ключ подлежал ротации.
- Корневой `.gitignore` игнорирует `*.db` (снимки прод-базы содержат реальные Telegram user ID),
  `appsettings*.json` и `tmp-find/`.
- `AdminUserIds` в конфиге — доступ к `/setrating` и кнопкам рейтинга.

## Известные ограничения (осознанные)

- Таймзоны только целочасовые (Индия +5:30 непредставима).
- Логирование — `Console.WriteLine` в systemd journal, без ILogger.
- Нет истории действий пользователя — только `LastInteractionAt` (последнее касание).
- Факт блокировки бота пользователем не отслеживается (403 при отправке просто логируется).
- Автоподписки видят только первые ~100 запусков (2 страницы API).

## Конвенции

- Коммиты — на английском (даже если общение на русском).
- Комментарии в новом коде — на русском (так сложилось в последних правках), в старом — английские.
- Тесты — только для чистой логики (форматтер, рейтинг, матчинг); БД и Telegram не мокаются.

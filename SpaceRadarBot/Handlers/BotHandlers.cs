using SpaceRadarBot.Data;
using SpaceRadarBot.Models;
using SpaceRadarBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.Configuration;

namespace SpaceRadarBot.Handlers;

public class BotHandlers
{
    private readonly ITelegramBotClient _botClient;
    private readonly LaunchService _launchService;
    private readonly DatabaseService _database;
    private readonly NotificationService _notificationService;
    private readonly List<long> _adminUserIds;
    private readonly string _botUsername;

    // Пользователи, от которых ждём текст фидбэка следующим сообщением (/feedback без текста).
    // In-memory: при рестарте бота режим сбросится — пользователь просто повторит команду.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _awaitingFeedback = new();

    public BotHandlers(
        ITelegramBotClient botClient,
        LaunchService launchService,
        DatabaseService database,
        NotificationService notificationService,
        IConfiguration configuration,
        string botUsername)
    {
        _botClient = botClient;
        _launchService = launchService;
        _database = database;
        _notificationService = notificationService;
        _adminUserIds = configuration.GetSection("AdminUserIds").Get<List<long>>() ?? new List<long>();
        _botUsername = botUsername;
    }

    public async Task HandleUpdateAsync(Update update)
    {
        var userId = update.Message?.From?.Id ?? update.CallbackQuery?.From.Id;

        try
        {
            // Внутри try: сбой БД здесь не должен ронять обработку апдейта
            if (userId.HasValue)
            {
                _database.UpdateLastInteractionAt(userId.Value);
            }

            if (update.Message?.Text != null)
            {
                await HandleMessageAsync(update.Message);
            }
            else if (update.CallbackQuery != null)
            {
                await HandleCallbackQueryAsync(update.CallbackQuery);
            }
        }
        catch (Exception ex)
        {
            var kind = update.Message?.Text != null ? $"message \"{update.Message.Text}\"" :
                       update.CallbackQuery != null ? $"callback \"{update.CallbackQuery.Data}\"" : "unknown";
            Console.WriteLine($"❌ Update #{update.Id} from user {userId} ({kind}) failed: {ex}");
        }
    }

    private async Task HandleMessageAsync(Message message)
    {
        // Бот рассчитан на личные чаты: подписки и уведомления привязаны к user id,
        // а в группах уведомление всё равно ушло бы в личку, которой может не быть.
        if (message.Chat.Type != ChatType.Private || message.From == null)
            return;

        var chatId = message.Chat.Id;
        var userId = message.From.Id;
        var text = message.Text ?? "";

        if (!text.StartsWith('/'))
        {
            // Пользователь в режиме ожидания фидбэка — это сообщение и есть фидбэк
            if (_awaitingFeedback.ContainsKey(userId))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    await _botClient.SendMessage(chatId,
                        "Пожалуйста, отправьте фидбэк текстовым сообщением.",
                        disableNotification: false);
                    return;
                }

                _awaitingFeedback.TryRemove(userId, out _);
                await SubmitFeedbackAsync(chatId, message.From, text);
            }
            return;
        }

        // Любая команда отменяет режим ожидания фидбэка,
        // чтобы «/feedback → передумал → /next» не записал «/next»-намерение как отзыв.
        _awaitingFeedback.TryRemove(userId, out _);

        // Точное сопоставление команды: раньше StartsWith("/next") ловил и "/nextfoo".
        // Суффикс @botname поддерживаем, чужие @боты игнорируем.
        var command = text.Split(' ', 2)[0];
        var atIndex = command.IndexOf('@');
        if (atIndex >= 0)
        {
            if (!command[(atIndex + 1)..].Equals(_botUsername, StringComparison.OrdinalIgnoreCase))
                return;
            command = command[..atIndex];
        }

        switch (command)
        {
            case "/start":
                await HandleStartCommand(chatId);
                break;
            case "/next":
                await HandleNextCommand(chatId, userId);
                break;
            case "/settings":
                await HandleSettingsCommand(chatId, userId);
                break;
            case "/timezone":
                await HandleTimezoneCommand(chatId, userId, text);
                break;
            case "/count":
                await HandleCountCommand(chatId, userId);
                break;
            case "/setrating":
                await HandleSetRatingCommand(chatId, userId, text);
                break;
            case "/feedback":
                await HandleFeedbackCommand(chatId, message, text);
                break;
        }
    }

    // Не больше стольких фидбэков в час с одного пользователя — защита от случайного флуда.
    private const int MaxFeedbackPerHour = 5;

    private async Task HandleFeedbackCommand(long chatId, Message message, string text)
    {
        var parts = text.Split(' ', 2);
        var feedbackText = parts.Length > 1 ? parts[1].Trim() : "";

        if (string.IsNullOrWhiteSpace(feedbackText))
        {
            // Тап по команде в меню шлёт голый /feedback — ждём текст следующим сообщением
            _awaitingFeedback[message.From!.Id] = 0;
            await _botClient.SendMessage(chatId,
                "💬 Напишите ваш отзыв или идею следующим сообщением 👇\n\n" +
                "Оно попадёт напрямую разработчику. Любая команда отменит отправку.",
                disableNotification: false);
            return;
        }

        await SubmitFeedbackAsync(chatId, message.From!, feedbackText);
    }

    private async Task SubmitFeedbackAsync(long chatId, User from, string feedbackText)
    {
        var userId = from.Id;

        if (_database.CountRecentFeedback(userId, TimeSpan.FromHours(1)) >= MaxFeedbackPerHour)
        {
            await _botClient.SendMessage(chatId,
                "⏳ Слишком много сообщений подряд. Попробуйте снова через час.",
                disableNotification: false);
            return;
        }

        feedbackText = MessageFormatter.TruncateFeedback(feedbackText);
        _database.SaveFeedback(new Feedback
        {
            UserId = userId,
            Username = from.Username,
            Text = feedbackText,
            CreatedAt = DateTime.UtcNow
        });

        var adminMessage = MessageFormatter.FormatFeedbackAdminMessage(userId, from.Username, feedbackText);
        foreach (var adminId in _adminUserIds)
        {
            if (adminId == userId)
                continue; // админ шлёт фидбэк сам себе — подтверждения достаточно

            try
            {
                await _botClient.SendMessage(adminId, adminMessage, parseMode: ParseMode.Markdown, disableNotification: false);
            }
            catch (Exception ex)
            {
                // 403 (админ не начинал чат с ботом) не должен ронять команду — фидбэк уже в базе
                Console.WriteLine($"❌ Failed to forward feedback to admin {adminId}: {ex.Message}");
            }
        }

        await _botClient.SendMessage(chatId,
            "✅ Спасибо! Ваше сообщение передано разработчику.",
            disableNotification: false);
    }

    private async Task HandleStartCommand(long chatId)
    {
        var welcomeMessage = "🚀 Добро пожаловать в Space Radar Bot!\n\n" +
                           "Я помогу вам отслеживать предстоящие космические запуски и уведомлю вас перед стартом.\n\n" +
                           "Команды:\n" +
                           "/next - Показать 5 предстоящих запусков\n" +
                           "/settings - Настроить автоматические уведомления\n" +
                           "/timezone - Установить часовой пояс (например: /timezone +3)\n" +
                           "/count - Статистика ваших подписок\n" +
                           "/feedback - Отправить отзыв или идею разработчику\n\n" +
                           "Вы можете подписаться на уведомления о запуске, нажав кнопку под каждым запуском. " +
                           "Вы получите уведомление за 30 минут до старта!";

        await _botClient.SendMessage(chatId, welcomeMessage, disableNotification: false);
    }

    private async Task HandleNextCommand(long chatId, long userId)
    {
        var launches = await _launchService.GetUpcomingLaunchesAsync();

        if (launches.Count == 0)
        {
            await _botClient.SendMessage(chatId, "В данный момент предстоящие запуски не найдены. Попробуйте позже.", disableNotification: false);
            return;
        }

        var timezoneOffset = _database.GetUserTimezoneOffset(userId);

        foreach (var launch in launches)
        {
            var message = MessageFormatter.FormatLaunchMessage(launch, timezoneOffset, useRussian: true);
            var keyboard = CreateSubscribeButton(launch.Id, userId, launch);

            await _botClient.SendMessage(chatId, message, replyMarkup: keyboard, parseMode: ParseMode.Markdown, disableNotification: false);
            await Task.Delay(50);
        }
    }

    private async Task HandleSettingsCommand(long chatId, long userId)
    {
        var currentPreference = _database.GetUserPreference(userId);
        var message = "⚙️ Настройки автоматических уведомлений\n\n" +
                     "Выберите, о каких запусках вы хотите получать автоматические уведомления за 30 минут до старта:\n\n" +
                     $"Текущая настройка: {FormatPreference(currentPreference)}";

        var keyboard = CreateSettingsKeyboard(currentPreference);

        await _botClient.SendMessage(chatId, message, replyMarkup: keyboard, disableNotification: false);
    }

    private async Task HandleCountCommand(long chatId, long userId)
    {
        var (total, manual, automatic, pending) = _database.GetUserSubscriptionCounts(userId);
        var preference = _database.GetUserPreference(userId);

        var message = "📊 Статистика подписок\n\n" +
                     $"Всего подписок: {total}\n" +
                     $"├─ Ручных: {manual}\n" +
                     $"├─ Автоматических: {automatic}\n" +
                     $"└─ Ожидают отправки: {pending}\n\n" +
                     $"Текущая настройка: {FormatPreference(preference)}";

        await _botClient.SendMessage(chatId, message, disableNotification: false);
    }

    private async Task HandleTimezoneCommand(long chatId, long userId, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            var currentOffset = _database.GetUserTimezoneOffset(userId);
            var message = "🌍 Установка часового пояса\n\n" +
                         $"Текущий часовой пояс: UTC{MessageFormatter.FormatOffset(currentOffset)}\n\n" +
                         "Для изменения используйте:\n" +
                         "/timezone +3\n" +
                         "/timezone -5\n" +
                         "/timezone 0\n\n" +
                         "Например, для Москвы (MSK): /timezone +3";

            await _botClient.SendMessage(chatId, message, disableNotification: false);
            return;
        }

        var timezoneStr = parts[1];

        if (int.TryParse(timezoneStr, out var timezoneOffset))
        {
            if (timezoneOffset < -12 || timezoneOffset > 14)
            {
                await _botClient.SendMessage(chatId, "❌ Неверный часовой пояс. Допустимые значения: от -12 до +14", disableNotification: false);
                return;
            }

            _database.SetUserTimezoneOffset(userId, timezoneOffset);

            var message = $"✅ Часовой пояс установлен: UTC{MessageFormatter.FormatOffset(timezoneOffset)}\n\n" +
                         "Теперь все время будет отображаться в вашем часовом поясе!";

            await _botClient.SendMessage(chatId, message, disableNotification: false);
        }
        else
        {
            await _botClient.SendMessage(chatId, "❌ Неверный формат. Используйте: /timezone +3 или /timezone -5", disableNotification: false);
        }
    }

    private async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery)
    {
        var data = callbackQuery.Data ?? "";
        var userId = callbackQuery.From.Id;
        var chatId = callbackQuery.Message?.Chat.Id ?? 0;

        if (data.StartsWith("subscribe_"))
        {
            var launchId = data.Replace("subscribe_", "");
            await HandleSubscribe(chatId, userId, launchId, callbackQuery.Message?.MessageId ?? 0, callbackQuery.Id);
        }
        else if (data.StartsWith("unsubscribe_"))
        {
            var launchId = data.Replace("unsubscribe_", "");
            await HandleUnsubscribe(chatId, userId, launchId, callbackQuery.Message?.MessageId ?? 0, callbackQuery.Id);
        }
        else if (data.StartsWith("pref_"))
        {
            await HandlePreferenceChange(chatId, userId, data, callbackQuery.Message?.MessageId ?? 0, callbackQuery.Id);
        }
        else if (data.StartsWith("rate_"))
        {
            await HandleRatingChange(chatId, userId, data, callbackQuery.Message?.MessageId ?? 0, callbackQuery.Id);
        }
        else if (data.StartsWith("lang_"))
        {
            await HandleLanguageToggle(chatId, userId, data, callbackQuery.Message?.MessageId ?? 0, callbackQuery.Id);
        }
        else
        {
            await _botClient.AnswerCallbackQuery(callbackQuery.Id);
        }
    }

    private async Task HandleSubscribe(long chatId, long userId, string launchId, int messageId, string callbackQueryId)
    {
        if (_database.IsUserSubscribed(userId, launchId))
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "✅ Вы уже подписаны на этот запуск!");
            return;
        }

        var launch = await _launchService.GetLaunchByIdAsync(launchId);

        if (launch == null)
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Запуск не найден.", showAlert: true);
            return;
        }

        var notificationTime = DateTime.SpecifyKind(launch.LaunchTime.ToUniversalTime().AddMinutes(-30), DateTimeKind.Utc);

        if (notificationTime <= DateTime.UtcNow)
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ До этого запуска осталось менее 30 минут, подписаться невозможно.", showAlert: true);
            return;
        }

        _database.AddSubscription(userId, launchId, notificationTime, isAutomatic: false);

        var timezoneOffset = _database.GetUserTimezoneOffset(userId);
        var localNotificationTime = notificationTime.AddHours(timezoneOffset);

        await _botClient.AnswerCallbackQuery(callbackQueryId, $"✅ Подписка оформлена! Уведомление: {localNotificationTime:dd.MM HH:mm} (UTC{MessageFormatter.FormatOffset(timezoneOffset)})");

        var updatedKeyboard = CreateSubscribeButton(launchId, userId, launch, showingRussian: true);
        await TryEditReplyMarkup(chatId, messageId, updatedKeyboard);
    }

    private async Task HandleUnsubscribe(long chatId, long userId, string launchId, int messageId, string callbackQueryId)
    {
        if (!_database.IsUserSubscribed(userId, launchId))
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Вы не подписаны на этот запуск.");
            return;
        }

        var removed = _database.RemoveSubscription(userId, launchId);

        if (removed)
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "✅ Подписка отменена");

            var launch = await _launchService.GetLaunchByIdAsync(launchId);
            var updatedKeyboard = CreateSubscribeButton(launchId, userId, launch, showingRussian: true);
            await TryEditReplyMarkup(chatId, messageId, updatedKeyboard);
        }
        else
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Ошибка при отмене подписки.", showAlert: true);
        }
    }

    private InlineKeyboardMarkup CreateSubscribeButton(string launchId, long userId, Models.Launch? launch = null, bool showingRussian = true)
    {
        var buttons = new List<InlineKeyboardButton[]>();
        var isSubscribed = _database.IsUserSubscribed(userId, launchId);

        if (isSubscribed)
        {
            var subscription = _database.GetSubscriptionByUserAndLaunch(userId, launchId);
            if (subscription != null)
            {
                var timezoneOffset = _database.GetUserTimezoneOffset(userId);
                var localNotificationTime = subscription.NotificationTime.AddHours(timezoneOffset);
                var notificationTimeStr = $"{localNotificationTime:dd.MM HH:mm} (UTC{MessageFormatter.FormatOffset(timezoneOffset)})";

                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData($"❌ Отписаться (🕐 {notificationTimeStr})", $"unsubscribe_{launchId}")
                });
            }
            else
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData("❌ Отписаться", $"unsubscribe_{launchId}")
                });
            }
        }
        else
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🔔 Подписаться на уведомление", $"subscribe_{launchId}")
            });
        }

        // Add language toggle button if both translations exist
        if (launch != null && !string.IsNullOrEmpty(launch.DescriptionRu) && !string.IsNullOrEmpty(launch.Description))
        {
            if (showingRussian)
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData("🇬🇧 Показать оригинал", $"lang_{launchId}_en")
                });
            }
            else
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData("🇷🇺 Показать перевод", $"lang_{launchId}_ru")
                });
            }
        }

        if (IsAdmin(userId))
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("1⭐", $"rate_{launchId}_1"),
                InlineKeyboardButton.WithCallbackData("2⭐", $"rate_{launchId}_2"),
                InlineKeyboardButton.WithCallbackData("3⭐", $"rate_{launchId}_3"),
                InlineKeyboardButton.WithCallbackData("4⭐", $"rate_{launchId}_4"),
                InlineKeyboardButton.WithCallbackData("5⭐", $"rate_{launchId}_5")
            });
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private async Task HandlePreferenceChange(long chatId, long userId, string data, int messageId, string callbackQueryId)
    {
        var preference = data switch
        {
            "pref_all" => NotificationPreference.AllLaunches,
            "pref_5stars" => NotificationPreference.FiveStarsOnly,
            "pref_4plus" => NotificationPreference.FourStarsAndAbove,
            "pref_none" => NotificationPreference.None,
            _ => NotificationPreference.None
        };

        _database.SetUserPreference(userId, preference);

        if (preference != NotificationPreference.None)
        {
            await _notificationService.CreateAutomaticSubscriptionsForUser(userId);
        }

        await _botClient.AnswerCallbackQuery(callbackQueryId, $"✅ Настройка сохранена: {FormatPreference(preference)}");

        var message = "⚙️ Настройки автоматических уведомлений\n\n" +
                     "Выберите, о каких запусках вы хотите получать автоматические уведомления за 30 минут до старта:\n\n" +
                     $"Текущая настройка: {FormatPreference(preference)}";

        var keyboard = CreateSettingsKeyboard(preference);

        try
        {
            await _botClient.EditMessageText(chatId, messageId, message, replyMarkup: keyboard);
        }
        catch (Exception ex)
        {
            // Обычно «message is not modified» при повторном выборе той же настройки
            Console.WriteLine($"⚠️ Settings message edit failed for user {userId}: {ex.Message}");
        }
    }

    private InlineKeyboardMarkup CreateSettingsKeyboard(NotificationPreference currentPreference)
    {
        var buttons = new List<InlineKeyboardButton[]>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    currentPreference == NotificationPreference.AllLaunches ? "✅ Все запуски" : "Все запуски",
                    "pref_all"
                )
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    currentPreference == NotificationPreference.FiveStarsOnly ? "✅ Только 5⭐" : "Только 5⭐",
                    "pref_5stars"
                )
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    currentPreference == NotificationPreference.FourStarsAndAbove ? "✅ 4⭐ и выше" : "4⭐ и выше",
                    "pref_4plus"
                )
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    currentPreference == NotificationPreference.None ? "✅ Не получать" : "Не получать",
                    "pref_none"
                )
            }
        };

        return new InlineKeyboardMarkup(buttons);
    }

    private string FormatPreference(NotificationPreference preference)
    {
        return preference switch
        {
            NotificationPreference.AllLaunches => "🔔 Все запуски",
            NotificationPreference.FiveStarsOnly => "⭐⭐⭐⭐⭐ Только 5 звёзд",
            NotificationPreference.FourStarsAndAbove => "⭐⭐⭐⭐ 4 звезды и выше",
            NotificationPreference.None => "🔕 Не получать автоматические уведомления",
            _ => "🔕 Не настроено"
        };
    }

    private bool IsAdmin(long userId)
    {
        return _adminUserIds.Contains(userId);
    }

    private async Task HandleSetRatingCommand(long chatId, long userId, string text)
    {
        if (!IsAdmin(userId))
        {
            await _botClient.SendMessage(chatId, "❌ Недостаточно прав", disableNotification: false);
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !int.TryParse(parts[2], out var rating) || rating < 1 || rating > 5)
        {
            await _botClient.SendMessage(chatId, "❌ Формат: /setrating <launch_id> <1-5>", disableNotification: false);
            return;
        }

        var success = _database.UpdateSpectacleRating(parts[1], rating);
        if (success)
        {
            var stars = new string('⭐', rating);
            await _botClient.SendMessage(chatId, $"✅ Рейтинг обновлён: {stars}\n🔒 Установлен ручной приоритет (не будет изменён при синхронизации)", disableNotification: false);
        }
        else
        {
            await _botClient.SendMessage(chatId, "❌ Запуск не найден", disableNotification: false);
        }
    }

    private async Task HandleRatingChange(long chatId, long userId, string data, int messageId, string callbackQueryId)
    {
        if (!IsAdmin(userId))
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Недостаточно прав");
            return;
        }

        // Callback data может быть подделана клиентом — валидируем так же строго, как /setrating
        var parts = data.Split('_');
        if (parts.Length != 3 || !int.TryParse(parts[2], out var rating) || rating < 1 || rating > 5)
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Ошибка", showAlert: true);
            return;
        }

        var launchId = parts[1];

        var success = _database.UpdateSpectacleRating(launchId, rating);
        if (success)
        {
            var stars = new string('⭐', rating);
            await _botClient.AnswerCallbackQuery(callbackQueryId, $"✅ Рейтинг: {stars} 🔒", showAlert: true);

            var launch = await _launchService.GetLaunchByIdAsync(launchId);
            if (launch != null)
            {
                var timezoneOffset = _database.GetUserTimezoneOffset(userId);
                var message = MessageFormatter.FormatLaunchMessage(launch, timezoneOffset, useRussian: true);
                var keyboard = CreateSubscribeButton(launchId, userId, launch, showingRussian: true);

                try
                {
                    await _botClient.EditMessageText(chatId, messageId, message, replyMarkup: keyboard, parseMode: ParseMode.Markdown);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Rating message edit failed for launch {launchId}: {ex.Message}");
                }
            }
        }
        else
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Ошибка", showAlert: true);
        }
    }

    private async Task HandleLanguageToggle(long chatId, long userId, string data, int messageId, string callbackQueryId)
    {
        var parts = data.Split('_');
        if (parts.Length != 3)
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Ошибка");
            return;
        }

        var launchId = parts[1];
        var language = parts[2]; // "en" or "ru"
        var useRussian = language == "ru";

        var launch = await _launchService.GetLaunchByIdAsync(launchId);
        if (launch == null)
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Запуск не найден");
            return;
        }

        var timezoneOffset = _database.GetUserTimezoneOffset(userId);
        var message = MessageFormatter.FormatLaunchMessage(launch, timezoneOffset, useRussian);
        var keyboard = CreateSubscribeButton(launchId, userId, launch, showingRussian: useRussian);

        try
        {
            await _botClient.EditMessageText(chatId, messageId, message, replyMarkup: keyboard, parseMode: ParseMode.Markdown);
            await _botClient.AnswerCallbackQuery(callbackQueryId, useRussian ? "🇷🇺 Перевод" : "🇬🇧 Original");
        }
        catch (Exception ex)
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Ошибка при обновлении");
            Console.WriteLine($"❌ Language toggle failed for user {userId}: {ex}");
        }
    }

    private async Task TryEditReplyMarkup(long chatId, int messageId, InlineKeyboardMarkup keyboard)
    {
        if (chatId == 0 || messageId == 0)
            return;

        try
        {
            await _botClient.EditMessageReplyMarkup(chatId, messageId, replyMarkup: keyboard);
        }
        catch (Exception ex)
        {
            // Сообщение могло устареть или быть удалено — не критично, но логируем
            Console.WriteLine($"⚠️ Reply markup edit failed (chat {chatId}, message {messageId}): {ex.Message}");
        }
    }
}

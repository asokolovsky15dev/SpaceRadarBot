using SpaceRadarBot.Data;
using SpaceRadarBot.Models;
using SpaceRadarBot.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace SpaceRadarBot.Handlers;

public class BotHandlers
{
    private readonly ITelegramBotClient _botClient;
    private readonly LaunchService _launchService;
    private readonly DatabaseService _database;
    private readonly NotificationService _notificationService;

    public BotHandlers(
        ITelegramBotClient botClient,
        LaunchService launchService,
        DatabaseService database,
        NotificationService notificationService)
    {
        _botClient = botClient;
        _launchService = launchService;
        _database = database;
        _notificationService = notificationService;
    }

    public async Task HandleUpdateAsync(Update update)
    {
        try
        {
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
            Console.WriteLine($"Error handling update: {ex.Message}");
        }
    }

    private async Task HandleMessageAsync(Message message)
    {
        var chatId = message.Chat.Id;
        var text = message.Text ?? "";

        if (text.StartsWith("/start"))
        {
            await HandleStartCommand(chatId);
        }
        else if (text.StartsWith("/next"))
        {
            await HandleNextCommand(chatId, message.From?.Id ?? 0);
        }
        else if (text.StartsWith("/settings"))
        {
            await HandleSettingsCommand(chatId, message.From?.Id ?? 0);
        }
        else if (text.StartsWith("/count"))
        {
            await HandleCountCommand(chatId, message.From?.Id ?? 0);
        }
    }

    private async Task HandleStartCommand(long chatId)
    {
        var welcomeMessage = "🚀 Добро пожаловать в Space Radar Bot!\n\n" +
                           "Я помогу вам отслеживать предстоящие космические запуски и уведомлю вас перед стартом.\n\n" +
                           "Команды:\n" +
                           "/next - Показать следующие 5 предстоящих запусков\n" +
                           "/settings - Настроить автоматические уведомления\n\n" +
                           "Вы можете подписаться на уведомления о запуске, нажав кнопку под каждым запуском. " +
                           "Вы получите уведомление за 30 минут до старта!";

        await _botClient.SendMessage(chatId, welcomeMessage, disableNotification: false);
    }

    private async Task HandleNextCommand(long chatId, long userId)
    {
        await _botClient.SendMessage(chatId, "🔍 Загружаю предстоящие запуски...", disableNotification: false);

        var launches = await _launchService.GetUpcomingLaunchesAsync();

        if (launches.Count == 0)
        {
            await _botClient.SendMessage(chatId, "В данный момент предстоящие запуски не найдены. Попробуйте позже.", disableNotification: false);
            return;
        }

        foreach (var launch in launches)
        {
            var message = FormatLaunchMessage(launch);
            var keyboard = CreateSubscribeButton(launch.Id, userId);

            await _botClient.SendMessage(chatId, message, replyMarkup: keyboard, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, disableNotification: false);
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

        var notificationTime = launch.LaunchTime.ToUniversalTime().AddMinutes(-30);

        if (notificationTime <= DateTime.UtcNow)
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ До этого запуска осталось менее 30 минут, подписаться невозможно.", showAlert: true);
            return;
        }

        _database.AddSubscription(userId, launchId, notificationTime, isAutomatic: false);

        await _botClient.AnswerCallbackQuery(callbackQueryId, $"✅ Подписка оформлена! Уведомление: {notificationTime:dd.MM HH:mm} UTC");

        var updatedKeyboard = CreateUnsubscribeButton(launchId, notificationTime);
        try
        {
            await _botClient.EditMessageReplyMarkup(chatId, messageId, replyMarkup: updatedKeyboard);
        }
        catch { }
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

            var updatedKeyboard = CreateSubscribeButton(launchId, userId);
            try
            {
                await _botClient.EditMessageReplyMarkup(chatId, messageId, replyMarkup: updatedKeyboard);
            }
            catch { }
        }
        else
        {
            await _botClient.AnswerCallbackQuery(callbackQueryId, "❌ Ошибка при отмене подписки.", showAlert: true);
        }
    }

    private string FormatLaunchMessage(Models.Launch launch)
    {
        var stars = new string('⭐', launch.SpectacleRating);
        var country = GetCountryDisplay(launch.CountryCode);

        var message = $"🚀 *{launch.Name}*\n\n" +
                     $"📍 {country}\n" +
                     $"🕐 {launch.LaunchTime:dd MMM yyyy, HH:mm} UTC\n" +
                     $"✨ {stars}";

        if (!string.IsNullOrEmpty(launch.Description))
        {
            message += $"\n\n{launch.Description}";
        }

        return message;
    }

    private string GetCountryDisplay(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode))
            return "🌍 Unknown";

        return countryCode.ToUpper() switch
        {
            "US" => "🇺🇸 USA",
            "RU" => "🇷🇺 Russia",
            "CN" => "🇨🇳 China",
            "GF" => "🇪🇺 French Guiana",
            "IN" => "🇮🇳 India",
            "JP" => "🇯🇵 Japan",
            "NZ" => "🇳🇿 New Zealand",
            "KZ" => "🇰🇿 Kazakhstan",
            "FR" => "🇫🇷 France",
            "GB" => "🇬🇧 United Kingdom",
            "IT" => "🇮🇹 Italy",
            "IR" => "🇮🇷 Iran",
            "KR" => "🇰🇷 South Korea",
            "IL" => "🇮🇱 Israel",
            _ => $"🌍 {countryCode}"
        };
    }

    private InlineKeyboardMarkup CreateSubscribeButton(string launchId, long userId)
    {
        var isSubscribed = _database.IsUserSubscribed(userId, launchId);

        if (isSubscribed)
        {
            var subscription = _database.GetSubscriptionByUserAndLaunch(userId, launchId);
            if (subscription != null)
            {
                var notificationTimeStr = subscription.NotificationTime.ToString("dd.MM HH:mm");
                return new InlineKeyboardMarkup(
                    InlineKeyboardButton.WithCallbackData($"❌ Отписаться (🕐 {notificationTimeStr})", $"unsubscribe_{launchId}")
                );
            }
            return new InlineKeyboardMarkup(
                InlineKeyboardButton.WithCallbackData("❌ Отписаться", $"unsubscribe_{launchId}")
            );
        }
        else
        {
            return new InlineKeyboardMarkup(
                InlineKeyboardButton.WithCallbackData("🔔 Подписаться на уведомление", $"subscribe_{launchId}")
            );
        }
    }

    private InlineKeyboardMarkup CreateUnsubscribeButton(string launchId, DateTime notificationTime)
    {
        var notificationTimeStr = notificationTime.ToString("dd.MM HH:mm");
        return new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData($"❌ Отписаться (🕐 {notificationTimeStr})", $"unsubscribe_{launchId}")
        );
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
        catch { }
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
}

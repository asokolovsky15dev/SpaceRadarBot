using SpaceRadarBot.Data;
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

    public BotHandlers(
        ITelegramBotClient botClient,
        LaunchService launchService,
        DatabaseService database)
    {
        _botClient = botClient;
        _launchService = launchService;
        _database = database;
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
        else if (text.StartsWith("/previous"))
        {
            await HandlePreviousCommand(chatId);
        }
    }

    private async Task HandleStartCommand(long chatId)
    {
        var welcomeMessage = "🚀 Добро пожаловать в Space Radar Bot!\n\n" +
                           "Я помогу вам отслеживать предстоящие космические запуски и уведомлю вас перед стартом.\n\n" +
                           "Команды:\n" +
                           "/next - Показать следующие 5 предстоящих запусков\n" +
                           "/previous - Показать последние 5 завершённых запусков\n\n" +
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

            await _botClient.SendMessage(chatId, message, replyMarkup: keyboard, disableNotification: false);
            await Task.Delay(50);
        }
    }

    private async Task HandlePreviousCommand(long chatId)
    {
        await _botClient.SendMessage(chatId, "🔍 Загружаю завершённые запуски...", disableNotification: false);

        var launches = await _launchService.GetPreviousLaunchesAsync();

        if (launches.Count == 0)
        {
            await _botClient.SendMessage(chatId, "В данный момент завершённые запуски не найдены. Попробуйте позже.", disableNotification: false);
            return;
        }

        foreach (var launch in launches)
        {
            var message = FormatLaunchMessage(launch, isPrevious: true);
            await _botClient.SendMessage(chatId, message, disableNotification: false);
            await Task.Delay(50);
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
            await HandleSubscribe(chatId, userId, launchId, callbackQuery.Message?.MessageId ?? 0);
        }

        await _botClient.AnswerCallbackQuery(callbackQuery.Id);
    }

    private async Task HandleSubscribe(long chatId, long userId, string launchId, int messageId)
    {
        if (_database.IsUserSubscribed(userId, launchId))
        {
            await _botClient.SendMessage(chatId, "✅ Вы уже подписаны на этот запуск!", disableNotification: false);
            return;
        }

        var launch = await _launchService.GetLaunchByIdAsync(launchId);
        
        if (launch == null)
        {
            await _botClient.SendMessage(chatId, "❌ Запуск не найден.", disableNotification: false);
            return;
        }

        var notificationTime = launch.LaunchTime.ToUniversalTime().AddMinutes(-30);
        
        if (notificationTime <= DateTime.UtcNow)
        {
            await _botClient.SendMessage(chatId, "❌ До этого запуска осталось менее 30 минут, подписаться невозможно.", disableNotification: false);
            return;
        }

        _database.AddSubscription(userId, launchId, notificationTime);
        
        await _botClient.SendMessage(chatId, 
            $"✅ Подписка оформлена! Вы получите уведомление за 30 минут до запуска.\n" +
            $"🕐 Время уведомления: {notificationTime:yyyy-MM-dd HH:mm} UTC", disableNotification: false);

        var updatedKeyboard = CreateSubscribedButton();
        try
        {
            await _botClient.EditMessageReplyMarkup(chatId, messageId, replyMarkup: updatedKeyboard);
        }
        catch { }
    }

    private string FormatLaunchMessage(Models.Launch launch, bool isPrevious = false)
    {
        var stars = new string('⭐', launch.SpectacleRating);
        var emoji = isPrevious ? "✅" : "🚀";

        var message = $"{emoji} {launch.Name}\n\n" +
                     $"Ракета: {launch.RocketName}\n" +
                     $"Стартовая площадка: {launch.LaunchPad}\n" +
                     $"🕐 Время: {launch.LaunchTime:yyyy-MM-dd HH:mm} UTC\n" +
                     $"Зрелищность: {stars}";

        if (!string.IsNullOrEmpty(launch.Description))
        {
            var shortDescription = launch.Description.Length > 200 
                ? launch.Description.Substring(0, 200) + "..." 
                : launch.Description;
            message += $"\n\n📝 {shortDescription}";
        }

        if (!string.IsNullOrEmpty(launch.LiveStreamUrl))
        {
            message += $"\n\n🎥 {(isPrevious ? "Повтор" : "Прямая трансляция")}: {launch.LiveStreamUrl}";
        }

        return message;
    }

    private InlineKeyboardMarkup CreateSubscribeButton(string launchId, long userId)
    {
        var isSubscribed = _database.IsUserSubscribed(userId, launchId);
        var buttonText = isSubscribed ? "✅ Подписан" : "🔔 Подписаться на уведомление";
        
        return new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData(buttonText, $"subscribe_{launchId}")
        );
    }

    private InlineKeyboardMarkup CreateSubscribedButton()
    {
        return new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData("✅ Подписан", "subscribed")
        );
    }
}

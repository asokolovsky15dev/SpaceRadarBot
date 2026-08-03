using LiteDB;
using Microsoft.Extensions.Configuration;
using SpaceRadarBot.Data;
using SpaceRadarBot.Handlers;
using SpaceRadarBot.Services;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

BsonMapper.Global.RegisterType<DateTime>(
    serialize: dt => dt.ToUniversalTime(),
    deserialize: bson => DateTime.SpecifyKind(bson.AsDateTime, DateTimeKind.Utc));

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

var botToken = configuration["BotToken"]
    ?? Environment.GetEnvironmentVariable("BOT_TOKEN")
    ?? throw new Exception("Bot token not found! Set BOT_TOKEN environment variable or add to appsettings.json");

var dbPath = configuration["Database:Path"] ?? "spaceradar.db";

Console.WriteLine("🚀 Space Radar Bot starting...");
Console.WriteLine($"📂 Database: {dbPath}");

var botClient = new TelegramBotClient(botToken);

// Проверяем токен до старта сервисов: с битым токеном падаем сразу,
// а не молча спамим ошибками поллинга.
var me = await botClient.GetMe();
Console.WriteLine($"✅ Bot authorized: @{me.Username}");

using var database = new DatabaseService(dbPath);

var clearedPostponements = database.ClearStalePostponementNotifications();
if (clearedPostponements > 0)
{
    Console.WriteLine($"🧹 Cleared {clearedPostponements} stale pending postponement notification(s)");
}

// Initialize translation service if API key is provided
TranslationService? translationService = null;
var openAiApiKey = configuration["OpenAI:ApiKey"];
if (!string.IsNullOrWhiteSpace(openAiApiKey) && openAiApiKey != "your-openai-api-key-here")
{
    var openAiModel = configuration["OpenAI:Model"] ?? "gpt-4o-mini";
    translationService = new TranslationService(openAiApiKey, openAiModel);
    Console.WriteLine($"🌍 Translation service enabled (Model: {openAiModel})");
}
else
{
    Console.WriteLine("⚠️ Translation service disabled (no OpenAI API key configured)");
}

var syncIntervalMinutes = int.TryParse(configuration["Sync:IntervalMinutes"], out var parsedInterval) && parsedInterval > 0
    ? parsedInterval
    : 10;

var launchSyncService = new LaunchSyncService(database, translationService, syncIntervalMinutes);
launchSyncService.Start();

var launchService = new LaunchService(database);
var notificationService = new NotificationService(botClient, database, launchService);
var botHandlers = new BotHandlers(botClient, launchService, database, notificationService, configuration, me.Username ?? "");

notificationService.Start();
Console.WriteLine("✅ Notification service started");

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

var receiverOptions = new ReceiverOptions
{
    // Обрабатываем только то, с чем реально умеем работать
    AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
    // После простоя не разгребаем бэклог устаревших команд
    DropPendingUpdates = true
};

await SetupBotCommands(botClient);

botClient.StartReceiving(
    async (bot, update, ct) => await botHandlers.HandleUpdateAsync(update),
    (bot, ex, ct) =>
    {
        Console.WriteLine($"❌ Polling error: {ex}");
        return Task.CompletedTask;
    },
    receiverOptions,
    cancellationTokenSource.Token
);

Console.WriteLine($"✅ Bot started: @{me.Username}");
Console.WriteLine("Bot is running. Press Ctrl+C to stop...");

try
{
    await Task.Delay(Timeout.Infinite, cancellationTokenSource.Token);
}
catch (TaskCanceledException)
{
    Console.WriteLine("Shutdown signal received...");
}

launchSyncService.Stop();
notificationService.Stop();
Console.WriteLine("Bot stopped.");

return;

static async Task SetupBotCommands(ITelegramBotClient botClient)
{
    var commands = new[]
    {
        new BotCommand { Command = "next", Description = "Показать 5 предстоящих запусков" },
        new BotCommand { Command = "settings", Description = "Настроить автоматические уведомления" },
        new BotCommand { Command = "timezone", Description = "Установить часовой пояс" },
        new BotCommand { Command = "count", Description = "Статистика ваших подписок" },
        new BotCommand { Command = "feedback", Description = "Отправить отзыв или идею разработчику" }
    };

    await botClient.SetMyCommands(commands);
    Console.WriteLine("✅ Bot menu commands configured");
}

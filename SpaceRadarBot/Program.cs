using Microsoft.Extensions.Configuration;
using SpaceRadarBot.Data;
using SpaceRadarBot.Handlers;
using SpaceRadarBot.Services;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

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
var database = new DatabaseService(dbPath);

var launchSyncService = new LaunchSyncService(database);
launchSyncService.Start();

var launchService = new LaunchService(database);
var notificationService = new NotificationService(botClient, database, launchService);
var botHandlers = new BotHandlers(botClient, launchService, database, notificationService);

notificationService.Start();
Console.WriteLine("✅ Notification service started");

var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = []
};

await SetupBotCommands(botClient);

botClient.StartReceiving(
    async (bot, update, ct) => await botHandlers.HandleUpdateAsync(update),
    (bot, ex, ct) =>
    {
        Console.WriteLine($"❌ Polling error: {ex.Message}");
        return Task.CompletedTask;
    },
    receiverOptions
);

var me = await botClient.GetMe();
Console.WriteLine($"✅ Bot started: @{me.Username}");
Console.WriteLine("Bot is running. Press Ctrl+C to stop...");

var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

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
        new BotCommand { Command = "next", Description = "Показать следующие 5 предстоящих запусков" },
        new BotCommand { Command = "settings", Description = "Настроить автоматические уведомления" },
        new BotCommand { Command = "timezone", Description = "Установить часовой пояс" }
    };

    await botClient.SetMyCommands(commands);
    Console.WriteLine("✅ Bot menu commands configured");
}

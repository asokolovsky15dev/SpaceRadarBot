using SpaceRadarBot.Data;
using Telegram.Bot;

namespace SpaceRadarBot.Services;

public class NotificationService
{
    private readonly ITelegramBotClient _botClient;
    private readonly DatabaseService _database;
    private readonly LaunchService _launchService;
    private Timer? _timer;

    public NotificationService(
        ITelegramBotClient botClient,
        DatabaseService database,
        LaunchService launchService)
    {
        _botClient = botClient;
        _database = database;
        _launchService = launchService;
    }

    public void Start()
    {
        _timer = new Timer(async _ => await CheckAndSendNotifications(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    public void Stop()
    {
        _timer?.Dispose();
    }

    private async Task CheckAndSendNotifications()
    {
        try
        {
            var pendingNotifications = _database.GetPendingNotifications();

            foreach (var subscription in pendingNotifications)
            {
                var launch = await _launchService.GetLaunchByIdAsync(subscription.LaunchId);

                if (launch != null)
                {
                    var launchTimeUtc = launch.LaunchTime.ToUniversalTime();
                    var notificationTimeUtc = launchTimeUtc.AddMinutes(-30);
                    var now = DateTime.UtcNow;

                    if (Math.Abs((notificationTimeUtc - subscription.NotificationTime).TotalMinutes) > 5)
                    {
                        Console.WriteLine($"⚠️ Launch time changed for {launch.Name}. Skipping old notification.");
                        _database.MarkNotificationSent(subscription.Id);
                        continue;
                    }

                    if (notificationTimeUtc > now.AddMinutes(5))
                    {
                        Console.WriteLine($"⏰ Launch {launch.Name} rescheduled. Too early to notify.");
                        _database.MarkNotificationSent(subscription.Id);
                        continue;
                    }

                    var message = FormatNotificationMessage(launch);

                    try
                    {
                        await _botClient.SendMessage(subscription.UserId, message, disableNotification: false);
                        _database.MarkNotificationSent(subscription.Id);
                        Console.WriteLine($"✅ Notification sent to user {subscription.UserId} for launch {launch.Name}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send notification to user {subscription.UserId}: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"⚠️ Launch {subscription.LaunchId} not found in database. Marking as sent.");
                    _database.MarkNotificationSent(subscription.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in notification service: {ex.Message}");
        }
    }

    private string FormatNotificationMessage(Models.Launch launch)
    {
        var stars = new string('⭐', launch.SpectacleRating);

        var message = $"🚀 ВНИМАНИЕ! ЗАПУСК ЧЕРЕЗ 30 МИНУТ!\n\n" +
                     $"Миссия: {launch.Name}\n" +
                     $"Ракета: {launch.RocketName}\n" +
                     $"Стартовая площадка: {launch.LaunchPad}\n" +
                     $"🕐 Время: {launch.LaunchTime:yyyy-MM-dd HH:mm} UTC\n" +
                     $"Зрелищность: {stars}";

        if (!string.IsNullOrEmpty(launch.Description))
        {
            var shortDescription = launch.Description.Length > 150 
                ? launch.Description.Substring(0, 150) + "..." 
                : launch.Description;
            message += $"\n\n📝 {shortDescription}";
        }

        if (!string.IsNullOrEmpty(launch.LiveStreamUrl))
        {
            message += $"\n\n🎥 Смотреть прямой эфир: {launch.LiveStreamUrl}";
        }

        return message;
    }
}

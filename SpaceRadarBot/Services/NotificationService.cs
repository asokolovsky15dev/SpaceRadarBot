using SpaceRadarBot.Data;
using SpaceRadarBot.Models;
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
            await ProcessManualSubscriptions();
            await ProcessAutomaticSubscriptions();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in notification service: {ex.Message}");
        }
    }

    private async Task ProcessManualSubscriptions()
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
            Console.WriteLine($"Error processing manual subscriptions: {ex.Message}");
        }
    }

    private async Task ProcessAutomaticSubscriptions()
    {
        try
        {
            var upcomingLaunches = await _launchService.GetAllUpcomingLaunchesAsync();
            var usersWithPreferences = _database.GetUsersWithActivePreferences();

            foreach (var userId in usersWithPreferences)
            {
                await CreateAutomaticSubscriptionsForUser(userId, upcomingLaunches);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing automatic subscriptions: {ex.Message}");
        }
    }

    public async Task CreateAutomaticSubscriptionsForUser(long userId, List<Launch>? launches = null)
    {
        try
        {
            var preference = _database.GetUserPreference(userId);

            if (preference == NotificationPreference.None)
                return;

            var upcomingLaunches = launches ?? await _launchService.GetAllUpcomingLaunchesAsync();

            foreach (var launch in upcomingLaunches)
            {
                if (!ShouldNotifyUser(preference, launch.SpectacleRating))
                    continue;

                if (_database.IsUserSubscribed(userId, launch.Id))
                    continue;

                if (_database.IsBlacklisted(userId, launch.Id))
                    continue;

                var notificationTime = launch.LaunchTime.ToUniversalTime().AddMinutes(-30);

                if (notificationTime <= DateTime.UtcNow)
                    continue;

                _database.AddSubscription(userId, launch.Id, notificationTime, isAutomatic: true);
                Console.WriteLine($"🔔 Auto-subscribed user {userId} to launch {launch.Name} based on preference {preference}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating automatic subscriptions for user {userId}: {ex.Message}");
        }
    }

    private bool ShouldNotifyUser(NotificationPreference preference, int spectacleRating)
    {
        return preference switch
        {
            NotificationPreference.AllLaunches => true,
            NotificationPreference.FiveStarsOnly => spectacleRating == 5,
            NotificationPreference.FourStarsAndAbove => spectacleRating >= 4,
            _ => false
        };
    }

    private string FormatNotificationMessage(Launch launch)
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

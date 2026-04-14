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
                    var timezoneOffset = _database.GetUserTimezoneOffset(subscription.UserId);
                    var message = FormatNotificationMessage(launch, timezoneOffset);

                    try
                    {
                        await _botClient.SendMessage(subscription.UserId, message, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, disableNotification: false);
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
                await CleanupIncompatibleAutomaticSubscriptions(userId, upcomingLaunches);
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

                var notificationTime = DateTime.SpecifyKind(launch.LaunchTime.ToUniversalTime().AddMinutes(-30), DateTimeKind.Utc);

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

    private async Task CleanupIncompatibleAutomaticSubscriptions(long userId, List<Launch> upcomingLaunches)
    {
        try
        {
            var preference = _database.GetUserPreference(userId);

            if (preference == NotificationPreference.None)
                return;

            var automaticSubscriptions = _database.GetUserAutomaticSubscriptions(userId);
            var launchDict = upcomingLaunches.ToDictionary(l => l.Id);

            foreach (var subscription in automaticSubscriptions)
            {
                if (!launchDict.TryGetValue(subscription.LaunchId, out var launch))
                    continue;

                if (!ShouldNotifyUser(preference, launch.SpectacleRating))
                {
                    _database.RemoveSubscriptionById(subscription.Id);
                    Console.WriteLine($"🗑️ Removed automatic subscription for user {userId} from launch {launch.Name} (rating changed from user preference)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error cleaning up automatic subscriptions for user {userId}: {ex.Message}");
        }
    }

    private string FormatNotificationMessage(Launch launch, int timezoneOffset)
    {
        var stars = new string('⭐', launch.SpectacleRating);
        var country = GetCountryDisplay(launch.CountryCode);
        var formattedTime = LaunchService.FormatLaunchTime(launch.LaunchTime, timezoneOffset);

        var message = $"🚀 *ЗАПУСК ЧЕРЕЗ 30 МИНУТ!*\n\n" +
                     $"*{launch.Name}*\n\n" +
                     $"📍 {country}\n" +
                     $"🕐 {formattedTime}\n" +
                     $"✨ {stars}";

        // Add booster information if available
        if (!string.IsNullOrEmpty(launch.BoosterSerialNumber))
        {
            var flightInfo = "";
            if (launch.BoosterFlightNumber.HasValue)
            {
                var flightNum = launch.BoosterFlightNumber.Value;
                var flightOrdinal = FormatFlightNumber(flightNum);
                flightInfo = $" ({flightOrdinal} полёт)";
            }

            var reusedIcon = launch.BoosterReused == true ? "♻️" : "🆕";
            message += $"\n{reusedIcon} Бустер {launch.BoosterSerialNumber}{flightInfo}";

            if (launch.LandingAttempt == true)
            {
                message += "\n🎯 Посадка: ожидается";
            }
        }

        if (!string.IsNullOrEmpty(launch.Description))
        {
            message += $"\n\n{launch.Description}";
        }

        if (!string.IsNullOrEmpty(launch.LiveStreamUrl))
        {
            message += $"\n\n🎥 [Смотреть прямой эфир]({launch.LiveStreamUrl})";
        }

        return message;
    }

    private string FormatFlightNumber(int number)
    {
        return number switch
        {
            1 => "1-й",
            2 => "2-й",
            3 => "3-й",
            _ => $"{number}-й"
        };
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
}

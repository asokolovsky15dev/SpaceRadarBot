using SpaceRadarBot.Data;
using SpaceRadarBot.Models;
using Telegram.Bot;

namespace SpaceRadarBot.Services;

public class NotificationService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

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
        // Непериодический таймер: следующий тик взводится только после завершения текущего,
        // иначе долгий тик (медленный Telegram) накладывался бы на следующий и дублировал отправки.
        _timer = new Timer(async _ => await TickAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public void Stop()
    {
        _timer?.Dispose();
    }

    private async Task TickAsync()
    {
        try
        {
            await ProcessPostponementNotifications();
            await ProcessDueNotifications();
            await ProcessAutomaticSubscriptions();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Notification service tick failed: {ex}");
        }
        finally
        {
            try
            {
                _timer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Сервис остановлен во время тика — перевзводить нечего
            }
        }
    }

    private async Task ProcessPostponementNotifications()
    {
        try
        {
            var pendingPostponements = _database.GetPendingPostponementNotifications();

            foreach (var postponement in pendingPostponements)
            {
                // Новое время уже в прошлом — сообщение о переносе бессмысленно
                if (postponement.NewLaunchTime <= DateTime.UtcNow)
                {
                    _database.MarkPostponementNotificationSent(postponement.Id);
                    continue;
                }

                var timezoneOffset = _database.GetUserTimezoneOffset(postponement.UserId);
                var message = MessageFormatter.FormatPostponementMessage(
                    postponement.LaunchName,
                    postponement.OldLaunchTime,
                    postponement.NewLaunchTime,
                    timezoneOffset);

                try
                {
                    await _botClient.SendMessage(postponement.UserId, message, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, disableNotification: false);
                    _database.MarkPostponementNotificationSent(postponement.Id);
                    Console.WriteLine($"📣 Postponement notification sent to user {postponement.UserId} for launch {postponement.LaunchName}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to send postponement to user {postponement.UserId} (launch {postponement.LaunchName}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ProcessPostponementNotifications failed: {ex}");
        }
    }

    // Обрабатывает все подписки с наступившим временем уведомления — и ручные, и автоматические.
    private async Task ProcessDueNotifications()
    {
        try
        {
            var pendingNotifications = _database.GetPendingNotifications();

            foreach (var subscription in pendingNotifications)
            {
                var launch = await _launchService.GetLaunchByIdAsync(subscription.LaunchId);

                if (launch == null)
                {
                    Console.WriteLine($"⚠️ Launch {subscription.LaunchId} not found in database. Marking as sent.");
                    _database.MarkNotificationSent(subscription.Id);
                    continue;
                }

                // Фактические минуты до старта: после переносов уведомление может сработать
                // не за 30 минут, а позже — или когда запуск уже прошёл.
                var minutesUntilLaunch = (int)Math.Round((launch.LaunchTime - DateTime.UtcNow).TotalMinutes);

                if (minutesUntilLaunch <= 0)
                {
                    Console.WriteLine($"⚠️ Launch {launch.Name} already happened ({-minutesUntilLaunch} min ago). Skipping notification.");
                    _database.MarkNotificationSent(subscription.Id);
                    continue;
                }

                var timezoneOffset = _database.GetUserTimezoneOffset(subscription.UserId);
                var message = MessageFormatter.FormatNotificationMessage(launch, timezoneOffset, minutesUntilLaunch);

                try
                {
                    await _botClient.SendMessage(subscription.UserId, message, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, disableNotification: false);
                    _database.MarkNotificationSent(subscription.Id);
                    Console.WriteLine($"✅ Notification sent to user {subscription.UserId} for launch {launch.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Failed to send notification to user {subscription.UserId} (launch {launch.Name}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ProcessDueNotifications failed: {ex}");
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
                CleanupIncompatibleAutomaticSubscriptions(userId, upcomingLaunches);
                await CreateAutomaticSubscriptionsForUser(userId, upcomingLaunches);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ProcessAutomaticSubscriptions failed: {ex}");
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

            // Подписки и блэклист забираем одним запросом на пользователя,
            // а не парой запросов на каждый запуск каждую минуту.
            var subscribedLaunchIds = _database.GetUserSubscribedLaunchIds(userId);
            var blacklistedLaunchIds = _database.GetUserBlacklistedLaunchIds(userId);

            foreach (var launch in upcomingLaunches)
            {
                if (!preference.Matches(launch.SpectacleRating))
                    continue;

                if (subscribedLaunchIds.Contains(launch.Id))
                    continue;

                if (blacklistedLaunchIds.Contains(launch.Id))
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
            Console.WriteLine($"❌ CreateAutomaticSubscriptionsForUser failed for user {userId}: {ex}");
        }
    }

    private void CleanupIncompatibleAutomaticSubscriptions(long userId, List<Launch> upcomingLaunches)
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

                if (!preference.Matches(launch.SpectacleRating))
                {
                    _database.RemoveSubscriptionById(subscription.Id);
                    Console.WriteLine($"🗑️ Removed automatic subscription for user {userId} from launch {launch.Name} (rating changed from user preference)");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ CleanupIncompatibleAutomaticSubscriptions failed for user {userId}: {ex}");
        }
    }
}

using SpaceRadarBot.Data;
using SpaceRadarBot.Models;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;

namespace SpaceRadarBot.Services;

public class NotificationService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    // Telegram даёт боту ~30 сообщений/сек суммарно; держимся ниже с запасом,
    // отправляя пачками по MaxMessagesPerSecond с паузой до целой секунды.
    private const int MaxMessagesPerSecond = 20;

    // Автоподписки пересчитываются по событиям (после синка и после смены настроек);
    // редкий скан по таймеру — только страховка на случай пропущенного события.
    private const int AutoSubscriptionFallbackTicks = 30;

    private readonly ITelegramBotClient _botClient;
    private readonly DatabaseService _database;
    private readonly LaunchService _launchService;
    private Timer? _timer;
    private int _tickCount;

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

            _tickCount++;
            if (_tickCount % AutoSubscriptionFallbackTicks == 0)
            {
                await RunAutomaticSubscriptionsAsync();
            }
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

    /// <summary>
    /// Шлёт сообщения пачками по MaxMessagesPerSecond параллельно, добирая паузу до секунды
    /// между пачками. Последовательная отправка (~5-8 msg/сек из-за RTT) на популярном запуске
    /// растягивала бы рассылку на минуты; лимит Telegram ~30 msg/сек всё равно не превышаем.
    /// </summary>
    private static async Task SendThrottledAsync<T>(IReadOnlyList<T> items, Func<T, Task> sendOne)
    {
        for (var offset = 0; offset < items.Count; offset += MaxMessagesPerSecond)
        {
            var batch = items.Skip(offset).Take(MaxMessagesPerSecond).ToList();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await Task.WhenAll(batch.Select(sendOne));

            var remaining = TimeSpan.FromSeconds(1) - stopwatch.Elapsed;
            if (offset + MaxMessagesPerSecond < items.Count && remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }
        }
    }

    // 403 = пользователь заблокировал бота: помечаем и больше не шлём (флаг снимется,
    // когда пользователь снова напишет боту). Возвращает true, если ошибка обработана.
    private bool HandleBlockedUser(ApiRequestException ex, long userId)
    {
        if (ex.ErrorCode != 403)
            return false;

        _database.SetUserBlocked(userId, true);
        Console.WriteLine($"🚫 User {userId} blocked the bot — marked, notifications suspended");
        return true;
    }

    private async Task ProcessPostponementNotifications()
    {
        try
        {
            var pendingPostponements = _database.GetPendingPostponementNotifications();
            if (pendingPostponements.Count == 0)
                return;

            var blockedUsers = _database.GetBlockedUserIds();
            var toSend = new List<(PostponementNotification postponement, string message)>();

            foreach (var postponement in pendingPostponements)
            {
                // Новое время уже в прошлом — сообщение о переносе бессмысленно
                if (postponement.NewLaunchTime <= DateTime.UtcNow)
                {
                    _database.MarkPostponementNotificationSent(postponement.Id);
                    continue;
                }

                if (blockedUsers.Contains(postponement.UserId))
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

                toSend.Add((postponement, message));
            }

            await SendThrottledAsync(toSend, async item =>
            {
                try
                {
                    await _botClient.SendMessage(item.postponement.UserId, item.message, parseMode: ParseMode.Markdown, disableNotification: false);
                    _database.MarkPostponementNotificationSent(item.postponement.Id);
                    Console.WriteLine($"📣 Postponement notification sent to user {item.postponement.UserId} for launch {item.postponement.LaunchName}");
                }
                catch (ApiRequestException ex) when (HandleBlockedUser(ex, item.postponement.UserId))
                {
                    _database.MarkPostponementNotificationSent(item.postponement.Id);
                }
                catch (Exception ex)
                {
                    // Не помечаем отправленным — повторим следующим тиком
                    Console.WriteLine($"❌ Failed to send postponement to user {item.postponement.UserId} (launch {item.postponement.LaunchName}): {ex.Message}");
                }
            });
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
            if (pendingNotifications.Count == 0)
                return;

            var blockedUsers = _database.GetBlockedUserIds();
            var toSend = new List<(Subscription subscription, string message, string launchName)>();

            foreach (var subscription in pendingNotifications)
            {
                if (blockedUsers.Contains(subscription.UserId))
                {
                    _database.MarkNotificationSent(subscription.Id);
                    continue;
                }

                var launch = await _launchService.GetLaunchByIdAsync(subscription.LaunchId);

                if (launch == null)
                {
                    Console.WriteLine($"⚠️ Launch {subscription.LaunchId} not found in database. Marking as sent.");
                    _database.MarkNotificationSent(subscription.Id);
                    continue;
                }

                // Фактические минуты до старта: после переносов уведомление может сработать
                // не за 30 минут, а позже — или когда запуск уже прошёл.
                // Округляем вверх: тик раз в минуту срабатывает чуть позже отметки T-30,
                // и Round давал бы «29 минут» — Ceiling показывает честные «не позже чем через 30».
                var minutesUntilLaunch = (int)Math.Ceiling((launch.LaunchTime - DateTime.UtcNow).TotalMinutes);

                if (minutesUntilLaunch <= 0)
                {
                    Console.WriteLine($"⚠️ Launch {launch.Name} already happened ({-minutesUntilLaunch} min ago). Skipping notification.");
                    _database.MarkNotificationSent(subscription.Id);
                    continue;
                }

                var timezoneOffset = _database.GetUserTimezoneOffset(subscription.UserId);
                var message = MessageFormatter.FormatNotificationMessage(launch, timezoneOffset, minutesUntilLaunch);
                toSend.Add((subscription, message, launch.Name));
            }

            await SendThrottledAsync(toSend, async item =>
            {
                try
                {
                    await _botClient.SendMessage(item.subscription.UserId, item.message, parseMode: ParseMode.Markdown, disableNotification: false);
                    _database.MarkNotificationSent(item.subscription.Id);
                    Console.WriteLine($"✅ Notification sent to user {item.subscription.UserId} for launch {item.launchName}");
                }
                catch (ApiRequestException ex) when (HandleBlockedUser(ex, item.subscription.UserId))
                {
                    _database.MarkNotificationSent(item.subscription.Id);
                }
                catch (Exception ex)
                {
                    // Не помечаем отправленным — повторим следующим тиком
                    Console.WriteLine($"❌ Failed to send notification to user {item.subscription.UserId} (launch {item.launchName}): {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ ProcessDueNotifications failed: {ex}");
        }
    }

    /// <summary>
    /// Полный пересчёт автоподписок всех активных пользователей. Вызывается по событиям
    /// (после синка запусков, после смены настроек — точечно) и редким fallback-таймером,
    /// а не каждую минуту: это O(пользователи × запуски), на shared-core машине
    /// ежеминутный скан стал бы первым узким местом при росте аудитории.
    /// </summary>
    public async Task RunAutomaticSubscriptionsAsync()
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
            Console.WriteLine($"❌ RunAutomaticSubscriptionsAsync failed: {ex}");
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
            // а не парой запросов на каждый запуск.
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

using LiteDB;
using SpaceRadarBot.Models;

namespace SpaceRadarBot.Data;

public class DatabaseService : IDisposable
{
    // Notify subscribers about a postponement only if the new launch time is within this window.
    private const int PostponementNotificationWindowHours = 24;

    // Один LiteDatabase на процесс: в direct-режиме LiteDB держит файл эксклюзивно,
    // и параллельное открытие из таймеров/хендлеров кидало бы IOException.
    // Сам экземпляр потокобезопасен; _writeLock защищает только наши check-then-insert
    // последовательности от гонок (двойной клик, одновременные тики).
    private readonly LiteDatabase _db;
    private readonly object _writeLock = new();

    public DatabaseService(string dbPath = "spaceradar.db")
    {
        _db = new LiteDatabase(dbPath);
        InitializeDatabase();
    }

    public void Dispose() => _db.Dispose();

    private void InitializeDatabase()
    {
        var launches = _db.GetCollection<Launch>("launches");
        launches.EnsureIndex(x => x.LaunchTime);

        var userPreferences = _db.GetCollection<UserPreference>("userPreferences");
        userPreferences.EnsureIndex(x => x.UserId);

        var userBlacklist = _db.GetCollection<UserBlacklist>("userBlacklist");
        userBlacklist.EnsureIndex(x => x.UserId);
        userBlacklist.EnsureIndex(x => x.LaunchId);

        var subscriptions = _db.GetCollection<Subscription>("subscriptions");
        subscriptions.EnsureIndex(x => x.UserId);
        subscriptions.EnsureIndex(x => x.LaunchId);

        var feedback = _db.GetCollection<Feedback>("feedback");
        feedback.EnsureIndex(x => x.UserId);
    }

    public void SaveFeedback(Feedback item)
    {
        var feedback = _db.GetCollection<Feedback>("feedback");
        feedback.Insert(item);
    }

    // Для rate-limit команды /feedback: сколько фидбэков пользователь отправил за окно.
    public int CountRecentFeedback(long userId, TimeSpan window)
    {
        var since = DateTime.UtcNow - window;
        var feedback = _db.GetCollection<Feedback>("feedback");
        return feedback.Count(f => f.UserId == userId && f.CreatedAt >= since);
    }

    public void UpsertLaunches(List<Launch> launchList)
    {
        lock (_writeLock)
        {
            var launches = _db.GetCollection<Launch>("launches");
            var subscriptions = _db.GetCollection<Subscription>("subscriptions");
            var postponements = _db.GetCollection<PostponementNotification>("postponementNotifications");

            foreach (var launch in launchList)
            {
                var existingLaunch = launches.FindOne(l => l.Id == launch.Id);

                if (existingLaunch != null)
                {
                    var existingUtc = existingLaunch.LaunchTime.Kind == DateTimeKind.Utc
                        ? existingLaunch.LaunchTime
                        : existingLaunch.LaunchTime.ToUniversalTime();
                    var newUtc = launch.LaunchTime.ToUniversalTime();
                    var timeDifference = Math.Abs((existingUtc - newUtc).TotalMinutes);

                    if (timeDifference > 5)
                    {
                        var newNotificationTime = DateTime.SpecifyKind(
                            newUtc.AddMinutes(-30),
                            DateTimeKind.Utc);

                        var hoursUntilLaunch = (newUtc - DateTime.UtcNow).TotalHours;
                        var shouldNotifyAboutPostponement = hoursUntilLaunch <= PostponementNotificationWindowHours;

                        var affectedSubscriptions = subscriptions
                            .Find(s => s.LaunchId == launch.Id)
                            .ToList();

                        var createdAt = DateTime.UtcNow;
                        foreach (var subscription in affectedSubscriptions)
                        {
                            if (shouldNotifyAboutPostponement)
                            {
                                // Дедупликация: NET у Launch Library дрожит, и без неё
                                // каждый сдвиг плодил бы новое сообщение о переносе.
                                var pendingForUser = postponements.FindOne(p =>
                                    p.UserId == subscription.UserId && p.LaunchId == launch.Id && !p.Sent);

                                if (pendingForUser != null)
                                {
                                    // Старое время оставляем исходным — пользователю важна
                                    // общая картина «было → стало», а не каждый шаг.
                                    pendingForUser.NewLaunchTime = launch.LaunchTime;
                                    pendingForUser.CreatedAt = createdAt;
                                    postponements.Update(pendingForUser);
                                }
                                else
                                {
                                    postponements.Insert(new PostponementNotification
                                    {
                                        UserId = subscription.UserId,
                                        LaunchId = launch.Id,
                                        LaunchName = launch.Name,
                                        OldLaunchTime = existingLaunch.LaunchTime,
                                        NewLaunchTime = launch.LaunchTime,
                                        CreatedAt = createdAt,
                                        Sent = false
                                    });
                                }
                            }
                            subscription.NotificationTime = newNotificationTime;
                            subscription.NotificationSent = false;
                            subscriptions.Update(subscription);
                        }

                        if (affectedSubscriptions.Count > 0)
                        {
                            var notifiedSuffix = shouldNotifyAboutPostponement
                                ? $", queued {affectedSubscriptions.Count} postponement notification(s)"
                                : $" (launch is {hoursUntilLaunch:F1}h away, > {PostponementNotificationWindowHours}h threshold — no postponement notifications)";
                            Console.WriteLine($"🔔 Launch time changed for {launch.Name}. Rescheduled {affectedSubscriptions.Count} notification(s) to {newNotificationTime:HH:mm:ss} UTC{notifiedSuffix}");
                        }
                    }

                    // Preserve manual rating override
                    if (existingLaunch.ManualRatingOverride)
                    {
                        launch.SpectacleRating = existingLaunch.SpectacleRating;
                        launch.ManualRatingOverride = true;
                    }

                    // Preserve Russian description if not being updated
                    if (string.IsNullOrWhiteSpace(launch.DescriptionRu) && !string.IsNullOrWhiteSpace(existingLaunch.DescriptionRu))
                    {
                        launch.DescriptionRu = existingLaunch.DescriptionRu;
                    }
                }

                launches.Upsert(launch);
            }
        }
    }

    public List<Launch> GetUpcomingLaunches(int limit = 5)
    {
        var launches = _db.GetCollection<Launch>("launches");

        var now = DateTime.UtcNow;
        return launches
            .Find(l => l.LaunchTime > now)
            .OrderBy(l => l.LaunchTime)
            .Take(limit)
            .ToList();
    }

    public List<Launch> GetAllUpcomingLaunches()
    {
        var launches = _db.GetCollection<Launch>("launches");

        var now = DateTime.UtcNow;
        return launches
            .Find(l => l.LaunchTime > now)
            .OrderBy(l => l.LaunchTime)
            .ToList();
    }

    public Launch? GetLaunchById(string launchId)
    {
        var launches = _db.GetCollection<Launch>("launches");
        return launches.FindOne(l => l.Id == launchId);
    }

    public void RemoveOldLaunches(int daysOld = 30)
    {
        var launches = _db.GetCollection<Launch>("launches");

        var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);
        launches.DeleteMany(l => l.LaunchTime < cutoffDate);
    }

    /// <summary>
    /// Удаляет данные, ссылающиеся на уже вычищенные запуски (подписки, блэклист),
    /// и доставленные уведомления о переносах. Без этого коллекции растут бесконечно.
    /// </summary>
    public void CleanupOrphanedData()
    {
        lock (_writeLock)
        {
            var launches = _db.GetCollection<Launch>("launches");
            var subscriptions = _db.GetCollection<Subscription>("subscriptions");
            var blacklist = _db.GetCollection<UserBlacklist>("userBlacklist");
            var postponements = _db.GetCollection<PostponementNotification>("postponementNotifications");

            var validLaunchIds = new HashSet<string>(launches.FindAll().Select(l => l.Id));

            var orphanedSubscriptionIds = subscriptions.FindAll()
                .Where(s => !validLaunchIds.Contains(s.LaunchId))
                .Select(s => s.Id)
                .ToList();
            foreach (var id in orphanedSubscriptionIds)
                subscriptions.Delete(id);

            var orphanedBlacklistIds = blacklist.FindAll()
                .Where(b => !validLaunchIds.Contains(b.LaunchId))
                .Select(b => b.Id)
                .ToList();
            foreach (var id in orphanedBlacklistIds)
                blacklist.Delete(id);

            var removedPostponements = postponements.DeleteMany(p => p.Sent);

            if (orphanedSubscriptionIds.Count > 0 || orphanedBlacklistIds.Count > 0 || removedPostponements > 0)
            {
                Console.WriteLine($"🧹 Cleanup: {orphanedSubscriptionIds.Count} orphaned subscription(s), " +
                                  $"{orphanedBlacklistIds.Count} orphaned blacklist entrie(s), " +
                                  $"{removedPostponements} delivered postponement notification(s) removed");
            }
        }
    }

    public void AddSubscription(long userId, string launchId, DateTime notificationTime, bool isAutomatic = false)
    {
        lock (_writeLock)
        {
            var subscriptions = _db.GetCollection<Subscription>("subscriptions");

            var existing = subscriptions.FindOne(s => s.UserId == userId && s.LaunchId == launchId);
            if (existing != null)
                return;

            subscriptions.Insert(new Subscription
            {
                UserId = userId,
                LaunchId = launchId,
                NotificationTime = notificationTime,
                NotificationSent = false,
                IsAutomatic = isAutomatic
            });
        }
    }

    public List<Subscription> GetPendingNotifications()
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");

        var now = DateTime.UtcNow;
        return subscriptions
            .Find(s => !s.NotificationSent && s.NotificationTime <= now)
            .ToList();
    }

    public void MarkNotificationSent(int subscriptionId)
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");

        var subscription = subscriptions.FindById(subscriptionId);
        if (subscription != null)
        {
            subscription.NotificationSent = true;
            subscriptions.Update(subscription);
        }
    }

    public List<PostponementNotification> GetPendingPostponementNotifications()
    {
        var postponements = _db.GetCollection<PostponementNotification>("postponementNotifications");
        return postponements.Find(p => !p.Sent).ToList();
    }

    public void MarkPostponementNotificationSent(int id)
    {
        var postponements = _db.GetCollection<PostponementNotification>("postponementNotifications");

        var notification = postponements.FindById(id);
        if (notification != null)
        {
            notification.Sent = true;
            postponements.Update(notification);
        }
    }

    /// <summary>
    /// Чистит на старте только реально протухшие переносы: старше maxAgeHours или те,
    /// чьё новое время запуска уже прошло. Свежие остаются в очереди и будут отправлены —
    /// раньше рестарт молча терял все неотправленные переносы.
    /// </summary>
    public int ClearStalePostponementNotifications(int maxAgeHours = 24)
    {
        var postponements = _db.GetCollection<PostponementNotification>("postponementNotifications");
        var cutoff = DateTime.UtcNow.AddHours(-maxAgeHours);
        var now = DateTime.UtcNow;
        return postponements.DeleteMany(p => !p.Sent && (p.CreatedAt < cutoff || p.NewLaunchTime <= now));
    }

    public bool IsUserSubscribed(long userId, string launchId)
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");
        return subscriptions.Exists(s => s.UserId == userId && s.LaunchId == launchId);
    }

    public Subscription? GetSubscriptionByUserAndLaunch(long userId, string launchId)
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");
        return subscriptions.FindOne(s => s.UserId == userId && s.LaunchId == launchId);
    }

    /// <summary>ID запусков, на которые пользователь подписан (одним запросом, для батч-проверок).</summary>
    public HashSet<string> GetUserSubscribedLaunchIds(long userId)
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");
        return subscriptions
            .Find(s => s.UserId == userId)
            .Select(s => s.LaunchId)
            .ToHashSet();
    }

    /// <summary>ID запусков в блэклисте пользователя (одним запросом, для батч-проверок).</summary>
    public HashSet<string> GetUserBlacklistedLaunchIds(long userId)
    {
        var blacklist = _db.GetCollection<UserBlacklist>("userBlacklist");
        return blacklist
            .Find(b => b.UserId == userId)
            .Select(b => b.LaunchId)
            .ToHashSet();
    }

    public bool RemoveSubscription(long userId, string launchId)
    {
        lock (_writeLock)
        {
            var subscriptions = _db.GetCollection<Subscription>("subscriptions");

            // Удаляем все записи пары (user, launch) — на случай исторических дубликатов
            var toRemove = subscriptions.Find(s => s.UserId == userId && s.LaunchId == launchId).ToList();
            foreach (var subscription in toRemove)
            {
                subscriptions.Delete(subscription.Id);
            }

            if (toRemove.Count == 0)
                return false;

            // Пользователь явно отписался — блэклистим запуск независимо от типа подписки,
            // иначе автоподписка вернула бы его в течение минуты.
            AddToBlacklist(userId, launchId);
            return true;
        }
    }

    public void SetUserPreference(long userId, NotificationPreference preference)
    {
        lock (_writeLock)
        {
            var userPreferences = _db.GetCollection<UserPreference>("userPreferences");

            var existing = userPreferences.FindOne(u => u.UserId == userId);
            var now = DateTime.UtcNow;

            if (existing != null)
            {
                existing.Preference = preference;
                existing.UpdatedAt = now;
                userPreferences.Update(existing);
            }
            else
            {
                userPreferences.Insert(new UserPreference
                {
                    UserId = userId,
                    Preference = preference,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            RemoveIncompatibleAutomaticSubscriptions(userId, preference);

            if (preference == NotificationPreference.None)
            {
                ClearUserBlacklist(userId);
            }
        }
    }

    public void RemoveIncompatibleAutomaticSubscriptions(long userId, NotificationPreference newPreference)
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");
        var launches = _db.GetCollection<Launch>("launches");

        var automaticSubscriptions = subscriptions.Find(s => s.UserId == userId && s.IsAutomatic && !s.NotificationSent).ToList();

        foreach (var subscription in automaticSubscriptions)
        {
            var launch = launches.FindOne(l => l.Id == subscription.LaunchId);

            if (launch == null || !newPreference.Matches(launch.SpectacleRating))
            {
                subscriptions.Delete(subscription.Id);
            }
        }
    }

    public void AddToBlacklist(long userId, string launchId)
    {
        lock (_writeLock)
        {
            var blacklist = _db.GetCollection<UserBlacklist>("userBlacklist");

            var existing = blacklist.FindOne(b => b.UserId == userId && b.LaunchId == launchId);
            if (existing != null)
                return;

            blacklist.Insert(new UserBlacklist
            {
                UserId = userId,
                LaunchId = launchId,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    public bool IsBlacklisted(long userId, string launchId)
    {
        var blacklist = _db.GetCollection<UserBlacklist>("userBlacklist");
        return blacklist.Exists(b => b.UserId == userId && b.LaunchId == launchId);
    }

    public void ClearUserBlacklist(long userId)
    {
        var blacklist = _db.GetCollection<UserBlacklist>("userBlacklist");
        blacklist.DeleteMany(b => b.UserId == userId);
    }

    public NotificationPreference GetUserPreference(long userId)
    {
        var userPreferences = _db.GetCollection<UserPreference>("userPreferences");

        var preference = userPreferences.FindOne(u => u.UserId == userId);
        return preference?.Preference ?? NotificationPreference.None;
    }

    public int GetUserTimezoneOffset(long userId)
    {
        var userPreferences = _db.GetCollection<UserPreference>("userPreferences");

        var preference = userPreferences.FindOne(u => u.UserId == userId);
        return preference?.TimezoneOffset ?? 0;
    }

    public void SetUserTimezoneOffset(long userId, int timezoneOffset)
    {
        lock (_writeLock)
        {
            var userPreferences = _db.GetCollection<UserPreference>("userPreferences");

            var existing = userPreferences.FindOne(u => u.UserId == userId);
            var now = DateTime.UtcNow;

            if (existing != null)
            {
                existing.TimezoneOffset = timezoneOffset;
                existing.UpdatedAt = now;
                userPreferences.Update(existing);
            }
            else
            {
                userPreferences.Insert(new UserPreference
                {
                    UserId = userId,
                    Preference = NotificationPreference.None,
                    TimezoneOffset = timezoneOffset,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
    }

    public void UpdateLastInteractionAt(long userId)
    {
        lock (_writeLock)
        {
            var userPreferences = _db.GetCollection<UserPreference>("userPreferences");
            var now = DateTime.UtcNow;

            var existing = userPreferences.FindOne(u => u.UserId == userId);
            if (existing != null)
            {
                existing.LastInteractionAt = now;
                userPreferences.Update(existing);
            }
            else
            {
                userPreferences.Insert(new UserPreference
                {
                    UserId = userId,
                    Preference = NotificationPreference.None,
                    TimezoneOffset = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastInteractionAt = now
                });
            }
        }
    }

    public List<long> GetUsersWithActivePreferences()
    {
        var userPreferences = _db.GetCollection<UserPreference>("userPreferences");

        return userPreferences
            .Find(u => u.Preference != NotificationPreference.None)
            .Select(u => u.UserId)
            .ToList();
    }

    public (int total, int manual, int automatic, int pending) GetUserSubscriptionCounts(long userId)
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");

        var userSubscriptions = subscriptions.Find(s => s.UserId == userId).ToList();

        var now = DateTime.UtcNow;
        var total = userSubscriptions.Count;
        var manual = userSubscriptions.Count(s => !s.IsAutomatic);
        var automatic = userSubscriptions.Count(s => s.IsAutomatic);
        // «Ожидают отправки» — только те, что реально будут отправлены (время впереди)
        var pending = userSubscriptions.Count(s => !s.NotificationSent && s.NotificationTime > now);

        return (total, manual, automatic, pending);
    }

    public bool UpdateSpectacleRating(string launchId, int rating)
    {
        lock (_writeLock)
        {
            var launches = _db.GetCollection<Launch>("launches");

            var launch = launches.FindOne(l => l.Id == launchId);
            if (launch == null)
                return false;

            launch.SpectacleRating = rating;
            launch.ManualRatingOverride = true;
            launch.LastUpdated = DateTime.UtcNow;

            return launches.Update(launch);
        }
    }

    public List<Subscription> GetUserAutomaticSubscriptions(long userId)
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");

        return subscriptions
            .Find(s => s.UserId == userId && s.IsAutomatic && !s.NotificationSent)
            .ToList();
    }

    public void RemoveSubscriptionById(int subscriptionId)
    {
        var subscriptions = _db.GetCollection<Subscription>("subscriptions");
        subscriptions.Delete(subscriptionId);
    }
}

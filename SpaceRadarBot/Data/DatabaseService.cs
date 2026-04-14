using LiteDB;
using SpaceRadarBot.Models;

namespace SpaceRadarBot.Data;

public class DatabaseService
{
    private readonly string _connectionString;

    public string ConnectionString => _connectionString;

    public DatabaseService(string dbPath = "spaceradar.db")
    {
        _connectionString = dbPath;
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");
        launches.EnsureIndex(x => x.LaunchTime);

        var userPreferences = db.GetCollection<UserPreference>("userPreferences");
        userPreferences.EnsureIndex(x => x.UserId);

        var userBlacklist = db.GetCollection<UserBlacklist>("userBlacklist");
        userBlacklist.EnsureIndex(x => x.UserId);
        userBlacklist.EnsureIndex(x => x.LaunchId);
    }

    public void UpsertLaunch(Launch launch)
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");
        launches.Upsert(launch);
    }

    public void UpsertLaunches(List<Launch> launchList)
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");
        var subscriptions = db.GetCollection<Subscription>("subscriptions");

        foreach (var launch in launchList)
        {
            var existingLaunch = launches.FindOne(l => l.Id == launch.Id);

            if (existingLaunch != null)
            {
                var timeDifference = Math.Abs((existingLaunch.LaunchTime - launch.LaunchTime).TotalMinutes);

                if (timeDifference > 5)
                {
                    var newNotificationTime = DateTime.SpecifyKind(
                        launch.LaunchTime.ToUniversalTime().AddMinutes(-30), 
                        DateTimeKind.Utc);

                    var affectedSubscriptions = subscriptions
                        .Find(s => s.LaunchId == launch.Id && !s.NotificationSent)
                        .ToList();

                    foreach (var subscription in affectedSubscriptions)
                    {
                        subscription.NotificationTime = newNotificationTime;
                        subscriptions.Update(subscription);
                    }

                    if (affectedSubscriptions.Count > 0)
                    {
                        Console.WriteLine($"🔔 Launch time changed for {launch.Name}. Rescheduled {affectedSubscriptions.Count} notification(s) to {newNotificationTime:HH:mm:ss} UTC");
                    }
                }

                // Preserve manual rating override
                if (existingLaunch.ManualRatingOverride)
                {
                    launch.SpectacleRating = existingLaunch.SpectacleRating;
                    launch.ManualRatingOverride = true;
                }
            }

            launches.Upsert(launch);
        }
    }

    public List<Launch> GetUpcomingLaunches(int limit = 5)
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");

        var now = DateTime.UtcNow;
        return launches
            .Find(l => l.LaunchTime > now)
            .OrderBy(l => l.LaunchTime)
            .Take(limit)
            .ToList();
    }

    public List<Launch> GetAllUpcomingLaunches()
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");

        var now = DateTime.UtcNow;
        return launches
            .Find(l => l.LaunchTime > now)
            .OrderBy(l => l.LaunchTime)
            .ToList();
    }

    public Launch? GetLaunchById(string launchId)
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");
        return launches.FindOne(l => l.Id == launchId);
    }

    public void RemoveOldLaunches(int daysOld = 30)
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");

        var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);
        launches.DeleteMany(l => l.LaunchTime < cutoffDate);
    }

    public void AddSubscription(long userId, string launchId, DateTime notificationTime, bool isAutomatic = false)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");

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

    public List<Subscription> GetPendingNotifications()
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");

        var now = DateTime.UtcNow;
        return subscriptions
            .Find(s => !s.NotificationSent && s.NotificationTime <= now)
            .ToList();
    }

    public void MarkNotificationSent(int subscriptionId)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");

        var subscription = subscriptions.FindById(subscriptionId);
        if (subscription != null)
        {
            subscription.NotificationSent = true;
            subscriptions.Update(subscription);
        }
    }

    public bool IsUserSubscribed(long userId, string launchId)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");
        return subscriptions.Exists(s => s.UserId == userId && s.LaunchId == launchId);
    }

    public Subscription? GetSubscriptionByUserAndLaunch(long userId, string launchId)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");
        return subscriptions.FindOne(s => s.UserId == userId && s.LaunchId == launchId);
    }

    public bool RemoveSubscription(long userId, string launchId)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");

        var subscription = subscriptions.FindOne(s => s.UserId == userId && s.LaunchId == launchId);
        if (subscription != null)
        {
            subscriptions.Delete(subscription.Id);

            if (!subscription.IsAutomatic)
            {
                AddToBlacklist(userId, launchId);
            }

            return true;
        }

        return false;
    }

    public void SetUserPreference(long userId, NotificationPreference preference)
    {
        using var db = new LiteDatabase(_connectionString);
        var userPreferences = db.GetCollection<UserPreference>("userPreferences");

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

    public void RemoveIncompatibleAutomaticSubscriptions(long userId, NotificationPreference newPreference)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");
        var launches = db.GetCollection<Launch>("launches");

        var automaticSubscriptions = subscriptions.Find(s => s.UserId == userId && s.IsAutomatic && !s.NotificationSent).ToList();

        foreach (var subscription in automaticSubscriptions)
        {
            var launch = launches.FindOne(l => l.Id == subscription.LaunchId);

            if (launch == null || !MatchesPreference(newPreference, launch.SpectacleRating))
            {
                subscriptions.Delete(subscription.Id);
            }
        }
    }

    private bool MatchesPreference(NotificationPreference preference, int spectacleRating)
    {
        return preference switch
        {
            NotificationPreference.AllLaunches => true,
            NotificationPreference.FiveStarsOnly => spectacleRating == 5,
            NotificationPreference.FourStarsAndAbove => spectacleRating >= 4,
            NotificationPreference.None => false,
            _ => false
        };
    }

    public void AddToBlacklist(long userId, string launchId)
    {
        using var db = new LiteDatabase(_connectionString);
        var blacklist = db.GetCollection<UserBlacklist>("userBlacklist");

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

    public bool IsBlacklisted(long userId, string launchId)
    {
        using var db = new LiteDatabase(_connectionString);
        var blacklist = db.GetCollection<UserBlacklist>("userBlacklist");
        return blacklist.Exists(b => b.UserId == userId && b.LaunchId == launchId);
    }

    public void ClearUserBlacklist(long userId)
    {
        using var db = new LiteDatabase(_connectionString);
        var blacklist = db.GetCollection<UserBlacklist>("userBlacklist");
        blacklist.DeleteMany(b => b.UserId == userId);
    }

    public NotificationPreference GetUserPreference(long userId)
    {
        using var db = new LiteDatabase(_connectionString);
        var userPreferences = db.GetCollection<UserPreference>("userPreferences");

        var preference = userPreferences.FindOne(u => u.UserId == userId);
        return preference?.Preference ?? NotificationPreference.None;
    }

    public int GetUserTimezoneOffset(long userId)
    {
        using var db = new LiteDatabase(_connectionString);
        var userPreferences = db.GetCollection<UserPreference>("userPreferences");

        var preference = userPreferences.FindOne(u => u.UserId == userId);
        return preference?.TimezoneOffset ?? 0;
    }

    public void SetUserTimezoneOffset(long userId, int timezoneOffset)
    {
        using var db = new LiteDatabase(_connectionString);
        var userPreferences = db.GetCollection<UserPreference>("userPreferences");

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

    public List<long> GetUsersWithActivePreferences()
    {
        using var db = new LiteDatabase(_connectionString);
        var userPreferences = db.GetCollection<UserPreference>("userPreferences");

        return userPreferences
            .Find(u => u.Preference != NotificationPreference.None)
            .Select(u => u.UserId)
            .ToList();
    }

    public (int total, int manual, int automatic, int pending) GetUserSubscriptionCounts(long userId)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");

        var userSubscriptions = subscriptions.Find(s => s.UserId == userId).ToList();

        var total = userSubscriptions.Count;
        var manual = userSubscriptions.Count(s => !s.IsAutomatic);
        var automatic = userSubscriptions.Count(s => s.IsAutomatic);
        var pending = userSubscriptions.Count(s => !s.NotificationSent);

        return (total, manual, automatic, pending);
    }

    public bool UpdateSpectacleRating(string launchId, int rating)
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");

        var launch = launches.FindOne(l => l.Id == launchId);
        if (launch == null)
            return false;

        launch.SpectacleRating = rating;
        launch.ManualRatingOverride = true;
        launch.LastUpdated = DateTime.UtcNow;

        return launches.Update(launch);
    }

    public List<Subscription> GetUserAutomaticSubscriptions(long userId)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");

        return subscriptions
            .Find(s => s.UserId == userId && s.IsAutomatic && !s.NotificationSent)
            .ToList();
    }

    public void RemoveSubscriptionById(int subscriptionId)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");
        subscriptions.Delete(subscriptionId);
    }
}

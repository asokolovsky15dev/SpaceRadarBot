using LiteDB;
using SpaceRadarBot.Models;

namespace SpaceRadarBot.Data;

public class DatabaseService
{
    private readonly string _connectionString;

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

        foreach (var launch in launchList)
        {
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

        RemoveAutomaticSubscriptions(userId);
    }

    public void RemoveAutomaticSubscriptions(long userId)
    {
        using var db = new LiteDatabase(_connectionString);
        var subscriptions = db.GetCollection<Subscription>("subscriptions");

        var automaticSubscriptions = subscriptions.Find(s => s.UserId == userId && s.IsAutomatic && !s.NotificationSent);

        foreach (var subscription in automaticSubscriptions)
        {
            subscriptions.Delete(subscription.Id);
        }
    }

    public NotificationPreference GetUserPreference(long userId)
    {
        using var db = new LiteDatabase(_connectionString);
        var userPreferences = db.GetCollection<UserPreference>("userPreferences");

        var preference = userPreferences.FindOne(u => u.UserId == userId);
        return preference?.Preference ?? NotificationPreference.None;
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
}

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

    public List<Launch> GetPreviousLaunches(int limit = 5)
    {
        using var db = new LiteDatabase(_connectionString);
        var launches = db.GetCollection<Launch>("launches");

        var now = DateTime.UtcNow;
        return launches
            .Find(l => l.LaunchTime <= now)
            .OrderByDescending(l => l.LaunchTime)
            .Take(limit)
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

    public void AddSubscription(long userId, string launchId, DateTime notificationTime)
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
            NotificationSent = false
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
}

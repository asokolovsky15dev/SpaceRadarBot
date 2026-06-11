using SpaceRadarBot.Data;
using SpaceRadarBot.Models;

namespace SpaceRadarBot.Services;

public class LaunchService
{
    private readonly DatabaseService _database;

    public LaunchService(DatabaseService database)
    {
        _database = database;
    }

    public Task<List<Launch>> GetUpcomingLaunchesAsync()
    {
        var launches = _database.GetUpcomingLaunches(5);
        return Task.FromResult(launches);
    }

    public Task<List<Launch>> GetAllUpcomingLaunchesAsync()
    {
        var launches = _database.GetAllUpcomingLaunches();
        return Task.FromResult(launches);
    }

    public Task<Launch?> GetLaunchByIdAsync(string launchId)
    {
        var launch = _database.GetLaunchById(launchId);
        return Task.FromResult(launch);
    }

    public static string FormatLaunchTime(DateTime utcTime, int timezoneOffset)
    {
        var localTime = utcTime.AddHours(timezoneOffset);
        var timezoneDisplay = timezoneOffset >= 0 ? $"+{timezoneOffset}" : $"{timezoneOffset}";
        return $"{localTime:dd MMM yyyy, HH:mm} (UTC{timezoneDisplay})";
    }
}

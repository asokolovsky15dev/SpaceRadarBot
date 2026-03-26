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
        Console.WriteLine("📦 Reading upcoming launches from database");

        var launches = _database.GetUpcomingLaunches(5);

        return Task.FromResult(launches);
    }

    public Task<List<Launch>> GetPreviousLaunchesAsync()
    {
        Console.WriteLine("📦 Reading previous launches from database");

        var launches = _database.GetPreviousLaunches(5);

        return Task.FromResult(launches);
    }

    public Task<Launch?> GetLaunchByIdAsync(string launchId)
    {
        var launch = _database.GetLaunchById(launchId);
        return Task.FromResult(launch);
    }
}

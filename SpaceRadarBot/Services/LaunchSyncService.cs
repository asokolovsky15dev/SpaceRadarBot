using System.Text.Json;
using SpaceRadarBot.Data;
using SpaceRadarBot.Models;

namespace SpaceRadarBot.Services;

public class LaunchSyncService
{
    private readonly HttpClient _httpClient;
    private readonly DatabaseService _database;
    private Timer? _timer;
    private const string BaseUrl = "https://ll.thespacedevs.com/2.3.0/launches";
    private const int SyncIntervalMinutes = 10;

    public LaunchSyncService(DatabaseService database)
    {
        _database = database;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SpaceRadarBot/1.0");
    }

    public void Start()
    {
        Console.WriteLine("🔄 Starting launch sync service...");
        _timer = new Timer(async _ => await SyncLaunches(), null, TimeSpan.Zero, TimeSpan.FromMinutes(SyncIntervalMinutes));
    }

    public void Stop()
    {
        _timer?.Dispose();
        Console.WriteLine("⏹️ Launch sync service stopped");
    }

    private async Task SyncLaunches()
    {
        try
        {
            Console.WriteLine($"🌐 [{DateTime.Now:HH:mm:ss}] Syncing launches from API...");

            var upcomingLaunches = await FetchAllLaunchesFromApi($"{BaseUrl}/upcoming/");
            var previousLaunches = await FetchLaunchesFromApi($"{BaseUrl}/previous/?limit=10");

            var allLaunches = upcomingLaunches.Concat(previousLaunches).ToList();

            if (allLaunches.Count > 0)
            {
                _database.UpsertLaunches(allLaunches);
                Console.WriteLine($"✅ [{DateTime.Now:HH:mm:ss}] Synced {allLaunches.Count} launches ({upcomingLaunches.Count} upcoming, {previousLaunches.Count} previous) to database");
            }
            else
            {
                Console.WriteLine($"⚠️ [{DateTime.Now:HH:mm:ss}] No launches fetched from API");
            }

            _database.RemoveOldLaunches(30);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error syncing launches: {ex.Message}");
        }
    }

    private async Task<List<Launch>> FetchAllLaunchesFromApi(string baseApiUrl)
    {
        var allLaunches = new List<Launch>();
        var offset = 0;
        const int limit = 100;
        var hasMore = true;

        try
        {
            while (hasMore)
            {
                var apiUrl = $"{baseApiUrl}?limit={limit}&offset={offset}";
                Console.WriteLine($"📥 Fetching page: offset={offset}, limit={limit}");

                var response = await _httpClient.GetStringAsync(apiUrl);
                var data = JsonSerializer.Deserialize<LaunchLibraryResponse>(response);

                if (data?.Results == null || data.Results.Count == 0)
                {
                    hasMore = false;
                    break;
                }

                var now = DateTime.UtcNow;
                var launches = data.Results.Select(l => new Launch
                {
                    Id = l.Id,
                    Name = l.Name,
                    RocketName = l.Rocket?.Configuration?.Name ?? "Unknown",
                    LaunchPad = FormatLaunchPad(l.Pad),
                    LaunchTime = l.Net.ToUniversalTime(),
                    LiveStreamUrl = GetLiveStreamUrl(l),
                    SpectacleRating = CalculateSpectacleRating(l),
                    Description = l.Mission?.Description,
                    LastUpdated = now,
                    CachedAt = now
                }).ToList();

                allLaunches.AddRange(launches);

                offset += limit;

                hasMore = data.Results.Count == limit;

                if (hasMore)
                {
                    await Task.Delay(500);
                }
            }

            Console.WriteLine($"📦 Total fetched: {allLaunches.Count} launches");
            return allLaunches;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error fetching all launches: {ex.Message}");
            return allLaunches;
        }
    }

    private async Task<List<Launch>> FetchLaunchesFromApi(string apiUrl)
    {
        try
        {
            var response = await _httpClient.GetStringAsync(apiUrl);
            var data = JsonSerializer.Deserialize<LaunchLibraryResponse>(response);

            if (data?.Results == null)
                return new List<Launch>();

            var now = DateTime.UtcNow;

            return data.Results.Select(l => new Launch
            {
                Id = l.Id,
                Name = l.Name,
                RocketName = l.Rocket?.Configuration?.Name ?? "Unknown",
                LaunchPad = FormatLaunchPad(l.Pad),
                LaunchTime = l.Net.ToUniversalTime(),
                LiveStreamUrl = GetLiveStreamUrl(l),
                SpectacleRating = CalculateSpectacleRating(l),
                Description = l.Mission?.Description,
                LastUpdated = now,
                CachedAt = now
            }).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching from {apiUrl}: {ex.Message}");
            return new List<Launch>();
        }
    }

    private string FormatLaunchPad(PadInfo? pad)
    {
        if (pad == null)
            return "Unknown";

        var location = pad.Location?.Name ?? "";
        return string.IsNullOrEmpty(location) ? pad.Name : $"{pad.Name}, {location}";
    }

    private string? GetLiveStreamUrl(LaunchLibraryLaunch launch)
    {
        if (launch.VidURLs != null && launch.VidURLs.Count > 0)
            return launch.VidURLs[0];
        
        return null;
    }

    private int CalculateSpectacleRating(LaunchLibraryLaunch launch)
    {
        int rating = 3;

        var rocketName = launch.Rocket?.Configuration?.Name?.ToLower() ?? "";
        
        if (rocketName.Contains("falcon heavy") || rocketName.Contains("starship"))
            rating = 5;
        else if (rocketName.Contains("falcon 9") || rocketName.Contains("ariane"))
            rating = 4;
        else if (rocketName.Contains("soyuz") || rocketName.Contains("atlas"))
            rating = 3;

        if (launch.WebcastLive || launch.VidURLs?.Count > 0)
            rating = Math.Min(5, rating + 1);

        return Math.Max(1, Math.Min(5, rating));
    }
}

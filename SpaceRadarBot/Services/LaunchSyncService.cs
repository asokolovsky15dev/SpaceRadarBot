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
            Console.WriteLine($"🌐 [{DateTime.Now:HH:mm:ss}] Syncing upcoming launches from API...");

            var upcomingLaunches = await FetchAllLaunchesFromApi($"{BaseUrl}/upcoming/");

            if (upcomingLaunches.Count > 0)
            {
                _database.UpsertLaunches(upcomingLaunches);
                Console.WriteLine($"✅ [{DateTime.Now:HH:mm:ss}] Synced {upcomingLaunches.Count} upcoming launches to database");
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
        try
        {
            var apiUrl = $"{baseApiUrl}?mode=detailed&limit=20";
            Console.WriteLine($"📥 Fetching launches from {apiUrl}");

            var response = await _httpClient.GetStringAsync(apiUrl);
            var data = JsonSerializer.Deserialize<LaunchLibraryResponse>(response);

            if (data?.Results == null || data.Results.Count == 0)
            {
                Console.WriteLine("⚠️ No results returned from API");
                return new List<Launch>();
            }

            var now = DateTime.UtcNow;
            var launches = data.Results.Select(l => new Launch
            {
                Id = l.Id,
                Name = l.Name,
                RocketName = l.Rocket?.Configuration?.Name ?? "Unknown",
                LaunchPad = FormatLaunchPad(l.Pad),
                CountryCode = l.LaunchServiceProvider?.Countries?.FirstOrDefault()?.Alpha2Code,
                LaunchTime = l.Net.ToUniversalTime(),
                LiveStreamUrl = GetLiveStreamUrl(l),
                SpectacleRating = CalculateSpectacleRating(l),
                Description = l.Mission?.Description,
                LastUpdated = now,
                CachedAt = now
            }).ToList();

            Console.WriteLine($"📦 Fetched {launches.Count} launches");
            return launches;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error fetching launches: {ex.Message}");
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
        if (launch.VidUrls == null || launch.VidUrls.Count == 0)
            return null;

        // Get video with highest priority (highest number = highest priority)
        var highestPriorityVideo = launch.VidUrls
            .Where(v => !string.IsNullOrEmpty(v.Url))
            .OrderByDescending(v => v.Priority)
            .FirstOrDefault();

        return highestPriorityVideo?.Url;
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

        return Math.Max(1, Math.Min(5, rating));
    }
}

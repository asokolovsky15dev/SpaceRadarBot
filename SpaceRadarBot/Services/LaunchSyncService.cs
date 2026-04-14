using SpaceRadarBot.Data;
using SpaceRadarBot.Models;
using System.Text.Json;

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
            var launches = data.Results.Select(l =>
            {
                // Extract first booster information (most launches have one core)
                var firstBooster = l.Rocket?.LauncherStage?.FirstOrDefault();

                return new Launch
                {
                    Id = l.Id,
                    Name = l.Name,
                    RocketName = l.Rocket?.Configuration?.Name ?? "Unknown",
                    LaunchPad = FormatLaunchPad(l.Pad),
                    CountryCode = l.LaunchServiceProvider?.Countries?.FirstOrDefault()?.Alpha2Code,
                    LaunchTime = DateTime.SpecifyKind(l.Net.ToUniversalTime(), DateTimeKind.Utc),
                    LiveStreamUrl = GetLiveStreamUrl(l),
                    SpectacleRating = CalculateSpectacleRating(l),
                    Description = l.Mission?.Description,
                    Orbit = l.Mission?.Orbit?.Abbrev,
                    BoosterSerialNumber = firstBooster?.Launcher?.SerialNumber,
                    BoosterFlightNumber = firstBooster?.LauncherFlightNumber,
                    BoosterReused = firstBooster?.Reused,
                    LandingAttempt = firstBooster?.Landing?.Attempt,
                    LastUpdated = now,
                    CachedAt = now
                };
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

        // Get video with highest priority (lower number = higher priority)
        var highestPriorityVideo = launch.VidUrls
            .Where(v => !string.IsNullOrEmpty(v.Url))
            .OrderBy(v => v.Priority)
            .FirstOrDefault();

        return highestPriorityVideo?.Url;
    }

    private int CalculateSpectacleRating(LaunchLibraryLaunch launch)
    {
        int rating = 3;

        var missionName = launch.Name?.ToLower() ?? "";
        var description = launch.Mission?.Description?.ToLower() ?? "";

        // Check for crewed missions (highest priority)
        var crewedKeywords = new[] { "crew", "crewed", "astronaut", "cosmonaut", "human", "manned", "iss crew" };
        if (crewedKeywords.Any(k => description.Contains(k) || missionName.Contains(k)))
        {
            return 5;
        }

        // Check for interplanetary/deep space missions
        var orbit = launch.Mission?.Orbit?.Abbrev?.ToLower() ?? "";
        var spectacularOrbits = new[]
        {
            "solar esc.", "jupiter orbit", "mars", "venus", "l2", "l1-point", "asteroid",
            "lo", "lunar flyby", "lunar impactor", "mars flyby", "venus flyby",
            "mercury flyby"
        };

        if (spectacularOrbits.Any(o => orbit.Contains(o.ToLower())))
        {
            return 5;
        }

        // Check rocket type
        var rocketName = launch.Rocket?.Configuration?.Name?.ToLower() ?? "";

        if (rocketName.Contains("falcon heavy") || rocketName.Contains("starship") || rocketName.Contains("sls") || rocketName.Contains("new glenn"))
            rating = 5;
        else if (rocketName.Contains("falcon 9") && !missionName.Contains("starlink"))
            rating = 4;

        // Upgrade rating for special missions (don't downgrade)
        var firstFlightKeywords = new[] { "maiden flight", "first flight", "inaugural", "debut" };
        if (firstFlightKeywords.Any(k => missionName.Contains(k) || description.Contains(k)))
        {
            rating = Math.Max(rating, 4);
        }

        var demoFlightKeywords = new[] { "demo flight", "test flight", "demonstration" };
        if (demoFlightKeywords.Any(k => missionName.Contains(k) || description.Contains(k)))
        {
            rating = Math.Max(rating, 4);
        }

        return Math.Max(1, Math.Min(5, rating));
    }
}

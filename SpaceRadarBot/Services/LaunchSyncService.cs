using SpaceRadarBot.Data;
using SpaceRadarBot.Models;
using System.Text.Json;

namespace SpaceRadarBot.Services;

public class LaunchSyncService
{
    private readonly HttpClient _httpClient;
    private readonly DatabaseService _database;
    private readonly TranslationService? _translationService;
    private readonly TimeSpan _syncInterval;
    private Timer? _timer;

    private const string BaseUrl = "https://ll.thespacedevs.com/2.3.0/launches";

    /// <summary>Вызывается после каждого успешного синка: рейтинги/времена могли измениться,
    /// автоподписки нужно пересчитать. Событийная модель вместо ежеминутного скана.</summary>
    public Func<Task>? LaunchesSynced { get; set; }

    // Анонимный лимит Launch Library — 15 запросов/час с IP.
    // При интервале 10 мин: 6 синков/час × 2 страницы = 12 запросов — с запасом.
    private const int MaxPagesPerSync = 2;
    private const int PageSize = 50;

    public LaunchSyncService(DatabaseService database, TranslationService? translationService = null, int syncIntervalMinutes = 10)
    {
        _database = database;
        _translationService = translationService;
        _syncInterval = TimeSpan.FromMinutes(syncIntervalMinutes);
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "SpaceRadarBot/1.0");
    }

    public void Start()
    {
        Console.WriteLine($"🔄 Starting launch sync service (every {_syncInterval.TotalMinutes:F0} min)...");
        // Непериодический таймер: следующий тик взводится после завершения текущего,
        // чтобы долгий синк (переводы, медленный API) не накладывался на следующий.
        _timer = new Timer(async _ => await SyncLaunches(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
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
                // Translate descriptions to Russian if translation service is available
                if (_translationService != null)
                {
                    await TranslateLaunchDescriptions(upcomingLaunches);
                }

                _database.UpsertLaunches(upcomingLaunches);
                Console.WriteLine($"✅ [{DateTime.Now:HH:mm:ss}] Synced {upcomingLaunches.Count} upcoming launches to database");

                if (LaunchesSynced != null)
                {
                    try
                    {
                        await LaunchesSynced();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Post-sync subscription refresh failed: {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"⚠️ [{DateTime.Now:HH:mm:ss}] No launches fetched from API");
            }

            _database.RemoveOldLaunches(30);
            _database.CleanupOrphanedData();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error syncing launches: {ex.Message}");
        }
        finally
        {
            try
            {
                _timer?.Change(_syncInterval, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Сервис остановлен во время синка
            }
        }
    }

    private async Task<List<Launch>> FetchAllLaunchesFromApi(string baseApiUrl)
    {
        try
        {
            var results = new List<LaunchLibraryLaunch>();
            var apiUrl = $"{baseApiUrl}?mode=detailed&limit={PageSize}";

            for (var page = 0; page < MaxPagesPerSync && apiUrl != null; page++)
            {
                Console.WriteLine($"📥 Fetching launches from {apiUrl}");

                var response = await _httpClient.GetStringAsync(apiUrl);
                var data = JsonSerializer.Deserialize<LaunchLibraryResponse>(response);

                if (data?.Results == null || data.Results.Count == 0)
                    break;

                results.AddRange(data.Results);
                apiUrl = data.Next;
            }

            if (results.Count == 0)
            {
                Console.WriteLine("⚠️ No results returned from API");
                return new List<Launch>();
            }

            var now = DateTime.UtcNow;
            var launches = results.Select(l =>
            {
                // Extract all booster information (for rockets like Falcon Heavy with multiple cores)
                var boosters = l.Rocket?.LauncherStage?.Select(stage => new BoosterInfo
                {
                    SerialNumber = stage.Launcher?.SerialNumber,
                    FlightNumber = stage.LauncherFlightNumber,
                    Reused = stage.Reused,
                    LandingAttempt = stage.Landing?.Attempt
                }).Where(b => !string.IsNullOrEmpty(b.SerialNumber)).ToList() ?? new List<BoosterInfo>();

                return new Launch
                {
                    Id = l.Id,
                    Name = l.Name,
                    RocketName = l.Rocket?.Configuration?.Name ?? "Unknown",
                    LaunchPad = FormatLaunchPad(l.Pad),
                    CountryCode = l.LaunchServiceProvider?.Countries?.FirstOrDefault()?.Alpha2Code,
                    LaunchTime = DateTime.SpecifyKind(l.Net.ToUniversalTime(), DateTimeKind.Utc),
                    LiveStreamUrl = GetLiveStreamUrl(l),
                    SpectacleRating = SpectacleRatingCalculator.Calculate(l),
                    Description = l.Mission?.Description,
                    Orbit = l.Mission?.Orbit?.Abbrev,
                    Boosters = boosters,
                    LastUpdated = now
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

    private async Task TranslateLaunchDescriptions(List<Launch> launches)
    {
        try
        {
            int translatedCount = 0;

            foreach (var launch in launches)
            {
                if (!string.IsNullOrWhiteSpace(launch.Description))
                {
                    // Check if translation already exists in DB
                    var existingLaunch = _database.GetLaunchById(launch.Id);
                    if (!string.IsNullOrWhiteSpace(existingLaunch?.DescriptionRu))
                    {
                        launch.DescriptionRu = existingLaunch!.DescriptionRu;
                        continue;
                    }

                    var translation = await _translationService!.TranslateToRussianAsync(launch.Description);
                    if (translation != null)
                    {
                        launch.DescriptionRu = translation;
                        translatedCount++;
                        Console.WriteLine($"✅ Translated: {launch.Name}");
                    }

                    await Task.Delay(200); // Rate limiting to avoid API throttling
                }
            }

            if (translatedCount > 0)
            {
                Console.WriteLine($"🌍 Successfully translated {translatedCount} descriptions to Russian");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Translation error: {ex.Message}");
        }
    }
}

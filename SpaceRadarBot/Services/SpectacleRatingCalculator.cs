using SpaceRadarBot.Models;

namespace SpaceRadarBot.Services;

/// <summary>
/// Эвристика «зрелищности» запуска (1–5⭐) по данным Launch Library.
/// Вынесена из LaunchSyncService, чтобы её можно было тестировать без сети и БД.
/// </summary>
public static class SpectacleRatingCalculator
{
    private static readonly string[] CrewedKeywords =
        { "crew", "crewed", "astronaut", "cosmonaut", "human", "manned", "iss crew" };

    private static readonly string[] SpectacularOrbits =
    {
        "solar esc.", "jupiter orbit", "mars", "venus", "l2", "l1-point", "asteroid",
        "lo", "lunar flyby", "lunar impactor", "mars flyby", "venus flyby",
        "mercury flyby"
    };

    private static readonly string[] FirstFlightKeywords =
        { "maiden flight", "first flight", "inaugural", "debut" };

    private static readonly string[] DemoFlightKeywords =
        { "demo flight", "test flight", "demonstration" };

    public static int Calculate(LaunchLibraryLaunch launch)
    {
        int rating = 3;

        var missionName = launch.Name?.ToLowerInvariant() ?? "";
        var description = launch.Mission?.Description?.ToLowerInvariant() ?? "";

        // Пилотируемые миссии — высший приоритет
        if (CrewedKeywords.Any(k => description.Contains(k) || missionName.Contains(k)))
        {
            return 5;
        }

        // Межпланетные / дальний космос
        var orbit = launch.Mission?.Orbit?.Abbrev?.ToLowerInvariant() ?? "";
        if (SpectacularOrbits.Any(o => orbit.Contains(o)))
        {
            return 5;
        }

        // Тип ракеты
        var rocketName = launch.Rocket?.Configuration?.Name?.ToLowerInvariant() ?? "";

        if (rocketName.Contains("falcon heavy") || rocketName.Contains("starship") ||
            rocketName.Contains("sls") || rocketName.Contains("new glenn"))
            rating = 5;
        else if (rocketName.Contains("falcon 9") && !missionName.Contains("starlink"))
            rating = 4;

        // Особые миссии повышают рейтинг (но не понижают)
        if (FirstFlightKeywords.Any(k => missionName.Contains(k) || description.Contains(k)))
        {
            rating = Math.Max(rating, 4);
        }

        if (DemoFlightKeywords.Any(k => missionName.Contains(k) || description.Contains(k)))
        {
            rating = Math.Max(rating, 4);
        }

        return Math.Clamp(rating, 1, 5);
    }
}

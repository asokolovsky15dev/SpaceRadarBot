using LiteDB;

namespace SpaceRadarBot.Models;

public class Launch
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string RocketName { get; set; } = string.Empty;
    public string LaunchPad { get; set; } = string.Empty;
    public string? CountryCode { get; set; }
    public DateTime LaunchTime { get; set; }
    public string? LiveStreamUrl { get; set; }
    public int SpectacleRating { get; set; }
    public bool ManualRatingOverride { get; set; } = false;
    public string? Description { get; set; }
    public string? DescriptionRu { get; set; }
    public string? Orbit { get; set; }

    // Booster information (supports multiple boosters like Falcon Heavy)
    public List<BoosterInfo> Boosters { get; set; } = new();

    public DateTime LastUpdated { get; set; }
    public DateTime CachedAt { get; set; }
}

public class BoosterInfo
{
    public string? SerialNumber { get; set; }
    public int? FlightNumber { get; set; }
    public bool? Reused { get; set; }
    public bool? LandingAttempt { get; set; }
}

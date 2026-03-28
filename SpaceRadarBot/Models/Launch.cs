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
    public string? Description { get; set; }
    public DateTime LastUpdated { get; set; }
    public DateTime CachedAt { get; set; }
}

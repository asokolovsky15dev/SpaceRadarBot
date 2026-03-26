using System.Text.Json.Serialization;

namespace SpaceRadarBot.Models;

public class LaunchLibraryResponse
{
    [JsonPropertyName("results")]
    public List<LaunchLibraryLaunch> Results { get; set; } = new();
}

public class LaunchLibraryLaunch
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("net")]
    public DateTime Net { get; set; }

    [JsonPropertyName("mission")]
    public MissionInfo? Mission { get; set; }

    [JsonPropertyName("rocket")]
    public RocketInfo? Rocket { get; set; }

    [JsonPropertyName("pad")]
    public PadInfo? Pad { get; set; }

    [JsonPropertyName("webcast_live")]
    public bool WebcastLive { get; set; }

    [JsonPropertyName("vidURLs")]
    public List<string>? VidURLs { get; set; }
}

public class RocketInfo
{
    [JsonPropertyName("configuration")]
    public RocketConfiguration? Configuration { get; set; }
}

public class RocketConfiguration
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class PadInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }
}

public class LocationInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class MissionInfo
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

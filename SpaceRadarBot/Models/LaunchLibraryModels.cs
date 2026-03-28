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

    [JsonPropertyName("vid_urls")]
    public List<VideoUrl>? VidUrls { get; set; }

    [JsonPropertyName("launch_service_provider")]
    public LaunchServiceProvider? LaunchServiceProvider { get; set; }
}

public class LaunchServiceProvider
{
    [JsonPropertyName("country")]
    public List<CountryInfo>? Countries { get; set; }
}

public class CountryInfo
{
    [JsonPropertyName("alpha_2_code")]
    public string? Alpha2Code { get; set; }
}

public class VideoUrl
{
    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
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

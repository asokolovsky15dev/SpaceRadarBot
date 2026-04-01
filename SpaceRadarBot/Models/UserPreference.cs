namespace SpaceRadarBot.Models;

public class UserPreference
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public NotificationPreference Preference { get; set; }
    public int TimezoneOffset { get; set; } = 0; // Hours offset from UTC (e.g., +3, -5)
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum NotificationPreference
{
    None = 0,
    AllLaunches = 1,
    FiveStarsOnly = 2,
    FourStarsAndAbove = 3
}

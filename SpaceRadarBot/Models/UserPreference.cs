namespace SpaceRadarBot.Models;

public class UserPreference
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public NotificationPreference Preference { get; set; }
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

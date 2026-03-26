namespace SpaceRadarBot.Models;

public class Subscription
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string LaunchId { get; set; } = string.Empty;
    public DateTime NotificationTime { get; set; }
    public bool NotificationSent { get; set; }
}

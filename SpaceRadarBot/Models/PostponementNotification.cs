namespace SpaceRadarBot.Models;

public class PostponementNotification
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string LaunchId { get; set; } = string.Empty;
    public string LaunchName { get; set; } = string.Empty;
    public DateTime OldLaunchTime { get; set; }
    public DateTime NewLaunchTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Sent { get; set; }
}

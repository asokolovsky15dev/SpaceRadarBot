namespace SpaceRadarBot.Models;

public class UserBlacklist
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string LaunchId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

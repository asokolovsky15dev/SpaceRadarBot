namespace SpaceRadarBot.Models;

public class Feedback
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string? Username { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

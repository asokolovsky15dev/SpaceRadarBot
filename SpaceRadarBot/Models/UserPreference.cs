namespace SpaceRadarBot.Models;

public class UserPreference
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public NotificationPreference Preference { get; set; }
    public int TimezoneOffset { get; set; } = 0; // Hours offset from UTC (e.g., +3, -5)
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastInteractionAt { get; set; }

    // Пользователь заблокировал бота (403 при отправке). Такие пропускаются в рассылках,
    // чтобы не молотить вечные 403 каждый тик. Сбрасывается при любом входящем сообщении.
    public bool IsBlocked { get; set; }
}

public enum NotificationPreference
{
    None = 0,
    AllLaunches = 1,
    FiveStarsOnly = 2,
    FourStarsAndAbove = 3
}

/// <summary>
/// Единственное место, где настройка уведомлений сопоставляется с рейтингом запуска.
/// Раньше логика дублировалась в DatabaseService и NotificationService.
/// </summary>
public static class NotificationPreferenceExtensions
{
    public static bool Matches(this NotificationPreference preference, int spectacleRating)
    {
        return preference switch
        {
            NotificationPreference.AllLaunches => true,
            NotificationPreference.FiveStarsOnly => spectacleRating == 5,
            NotificationPreference.FourStarsAndAbove => spectacleRating >= 4,
            _ => false
        };
    }
}

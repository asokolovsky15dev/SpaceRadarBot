using System.Globalization;
using SpaceRadarBot.Models;

namespace SpaceRadarBot.Services;

/// <summary>
/// Единая точка форматирования всех user-facing сообщений.
/// Используется и в BotHandlers (/next, переключение языка), и в NotificationService
/// (уведомления о старте и переносах), чтобы тексты не расходились между путями.
/// </summary>
public static class MessageFormatter
{
    // ru-RU с фолбэком на инвариант: если процесс запущен с
    // DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, создание культуры кидает исключение.
    private static readonly CultureInfo RuCulture = CreateRuCulture();

    private static CultureInfo CreateRuCulture()
    {
        try
        {
            return CultureInfo.GetCultureInfo("ru-RU");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    public static string FormatLaunchTime(DateTime utcTime, int timezoneOffset)
    {
        var localTime = utcTime.AddHours(timezoneOffset);
        return $"{localTime.ToString("dd MMM yyyy, HH:mm", RuCulture)} (UTC{FormatOffset(timezoneOffset)})";
    }

    public static string FormatOffset(int timezoneOffset) =>
        timezoneOffset >= 0 ? $"+{timezoneOffset}" : timezoneOffset.ToString();

    /// <summary>Карточка запуска для /next и переключения языка.
    /// Без ссылки на эфир: превью видео в Telegram раздувает сообщение.</summary>
    public static string FormatLaunchMessage(Launch launch, int timezoneOffset, bool useRussian)
    {
        return $"🚀 *{SanitizeMd(launch.Name)}*\n\n" +
               BuildLaunchBody(launch, timezoneOffset, useRussian, includeStreamLink: false);
    }

    /// <summary>Уведомление о скором запуске. Минуты — фактические, не захардкоженные 30.</summary>
    public static string FormatNotificationMessage(Launch launch, int timezoneOffset, int minutesUntilLaunch)
    {
        return $"🚀 *ЗАПУСК ЧЕРЕЗ {minutesUntilLaunch} {PluralizeMinutes(minutesUntilLaunch)}!*\n\n" +
               $"*{SanitizeMd(launch.Name)}*\n\n" +
               BuildLaunchBody(launch, timezoneOffset, useRussian: true, includeStreamLink: true);
    }

    public static string FormatPostponementMessage(string launchName, DateTime oldTime, DateTime newTime, int timezoneOffset)
    {
        var oldLocalTime = oldTime.AddHours(timezoneOffset);
        var newLocalTime = newTime.AddHours(timezoneOffset);
        var newNotificationTime = newTime.AddMinutes(-30).AddHours(timezoneOffset);
        var timezoneDisplay = FormatOffset(timezoneOffset);

        return $"⏰ *ПЕРЕНОС!*\n\n" +
               $"🚀 *{SanitizeMd(launchName)}*\n\n" +
               $"Старое время: {oldLocalTime:dd.MM.yyyy HH:mm} (UTC{timezoneDisplay})\n" +
               $"Новое время: {newLocalTime:dd.MM.yyyy HH:mm} (UTC{timezoneDisplay})\n\n" +
               $"Ваше уведомление перенесено на {newNotificationTime:dd.MM.yyyy HH:mm} (UTC{timezoneDisplay})";
    }

    private static string BuildLaunchBody(Launch launch, int timezoneOffset, bool useRussian, bool includeStreamLink)
    {
        var stars = new string('⭐', Math.Clamp(launch.SpectacleRating, 0, 5));
        var country = GetCountryDisplay(launch.CountryCode);
        var formattedTime = FormatLaunchTime(launch.LaunchTime, timezoneOffset);

        var message = $"📍 {country}\n" +
                      $"🕐 {formattedTime}\n" +
                      $"✨ {stars}";

        // Информация о бустерах (поддерживает несколько ядер — Falcon Heavy)
        if (launch.Boosters != null)
        {
            foreach (var booster in launch.Boosters)
            {
                if (string.IsNullOrEmpty(booster.SerialNumber))
                    continue;

                var flightInfo = booster.FlightNumber.HasValue
                    ? $" ({FormatFlightNumber(booster.FlightNumber.Value)} полёт)"
                    : "";

                var reusedIcon = booster.Reused == true ? "♻️" : "🆕";
                message += $"\n{reusedIcon} Бустер {SanitizeMd(booster.SerialNumber)}{flightInfo}";

                if (booster.LandingAttempt == true)
                {
                    message += " 🎯";
                }
            }
        }

        // Описание: русский перевод при наличии, иначе оригинал
        var hasRussian = !string.IsNullOrEmpty(launch.DescriptionRu);
        var hasEnglish = !string.IsNullOrEmpty(launch.Description);

        if (useRussian && hasRussian)
        {
            message += $"\n\n{SanitizeMd(launch.DescriptionRu!)}\n\n_Переведено с помощью AI_";
        }
        else if (hasEnglish)
        {
            message += $"\n\n{SanitizeMd(launch.Description!)}";
        }

        if (includeStreamLink && !string.IsNullOrEmpty(launch.LiveStreamUrl))
        {
            message += $"\n\n🎥 [Смотреть прямой эфир]({EscapeUrlForMarkdown(launch.LiveStreamUrl)})";
        }

        return message;
    }

    /// <summary>Максимальная длина текста фидбэка; лишнее обрезается с многоточием.</summary>
    public const int MaxFeedbackLength = 1000;

    public static string TruncateFeedback(string text)
    {
        text = text.Trim();
        return text.Length <= MaxFeedbackLength ? text : text[..MaxFeedbackLength] + "…";
    }

    /// <summary>Сообщение админу о новом фидбэке. Текст пользователя санитизируется,
    /// чтобы _*[` в нём не ломали Legacy Markdown.</summary>
    public static string FormatFeedbackAdminMessage(long userId, string? username, string text)
    {
        var who = string.IsNullOrEmpty(username) ? $"id {userId}" : $"@{SanitizeMd(username)} (id {userId})";
        return $"💬 *Новый фидбэк*\nОт: {who}\n\n{SanitizeMd(text)}";
    }

    // Legacy Markdown не поддерживает экранирование бэкслэшем, поэтому вычищаем
    // четыре спецсимвола (_, *, [, `) из данных API, чтобы не ломать парсер Telegram.
    public static string SanitizeMd(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("_", " ").Replace("*", "").Replace("[", "(").Replace("`", "'");
    }

    // Скобки в URL ломают конструкцию [text](url) — заменяем на percent-encoding.
    public static string EscapeUrlForMarkdown(string url) =>
        url.Replace("(", "%28").Replace(")", "%29");

    public static string FormatFlightNumber(int number) => $"{number}-й";

    public static string PluralizeMinutes(int n)
    {
        var abs = Math.Abs(n);
        if (abs % 100 is >= 11 and <= 14) return "МИНУТ";
        return (abs % 10) switch
        {
            1 => "МИНУТУ",
            2 or 3 or 4 => "МИНУТЫ",
            _ => "МИНУТ"
        };
    }

    public static string GetCountryDisplay(string? countryCode)
    {
        if (string.IsNullOrEmpty(countryCode))
            return "🌍 Unknown";

        return countryCode.ToUpperInvariant() switch
        {
            "US" => "🇺🇸 USA",
            "RU" => "🇷🇺 Russia",
            "CN" => "🇨🇳 China",
            "GF" => "🇪🇺 French Guiana",
            "IN" => "🇮🇳 India",
            "JP" => "🇯🇵 Japan",
            "NZ" => "🇳🇿 New Zealand",
            "KZ" => "🇰🇿 Kazakhstan",
            "FR" => "🇫🇷 France",
            "GB" => "🇬🇧 United Kingdom",
            "IT" => "🇮🇹 Italy",
            "IR" => "🇮🇷 Iran",
            "KR" => "🇰🇷 South Korea",
            "IL" => "🇮🇱 Israel",
            _ => $"🌍 {countryCode}"
        };
    }
}

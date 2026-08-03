using SpaceRadarBot.Models;
using SpaceRadarBot.Services;
using Xunit;

namespace SpaceRadarBot.Tests;

public class MessageFormatterTests
{
    [Theory]
    [InlineData("Falcon_9 | Starlink", "Falcon 9 | Starlink")] // подчёркивание → пробел
    [InlineData("Ariane *62*", "Ariane 62")]                   // звёздочки удаляются
    [InlineData("GTO [rideshare]", "GTO (rideshare]")]         // [ → (
    [InlineData("`code`", "'code'")]                           // бэктики → кавычки
    public void SanitizeMd_StripsLegacyMarkdownSpecials(string input, string expected)
    {
        Assert.Equal(expected, MessageFormatter.SanitizeMd(input));
    }

    [Fact]
    public void SanitizeMd_EmptyAndNull_ReturnedAsIs()
    {
        Assert.Equal("", MessageFormatter.SanitizeMd(""));
        Assert.Null(MessageFormatter.SanitizeMd(null!));
    }

    [Theory]
    [InlineData(1, "МИНУТУ")]
    [InlineData(2, "МИНУТЫ")]
    [InlineData(4, "МИНУТЫ")]
    [InlineData(5, "МИНУТ")]
    [InlineData(11, "МИНУТ")]  // 11-14 — всегда «минут»
    [InlineData(12, "МИНУТ")]
    [InlineData(21, "МИНУТУ")]
    [InlineData(22, "МИНУТЫ")]
    [InlineData(25, "МИНУТ")]
    [InlineData(30, "МИНУТ")]
    [InlineData(111, "МИНУТ")]
    [InlineData(121, "МИНУТУ")]
    public void PluralizeMinutes_UsesRussianRules(int minutes, string expected)
    {
        Assert.Equal(expected, MessageFormatter.PluralizeMinutes(minutes));
    }

    [Theory]
    [InlineData(3, "+3")]
    [InlineData(0, "+0")]
    [InlineData(-5, "-5")]
    public void FormatOffset_AlwaysSigned(int offset, string expected)
    {
        Assert.Equal(expected, MessageFormatter.FormatOffset(offset));
    }

    [Fact]
    public void FormatLaunchTime_AppliesOffsetAndLabel()
    {
        var utc = new DateTime(2026, 8, 30, 11, 26, 0, DateTimeKind.Utc);
        var formatted = MessageFormatter.FormatLaunchTime(utc, 3);

        Assert.Contains("14:26", formatted); // 11:26 UTC + 3
        Assert.Contains("(UTC+3)", formatted);
        Assert.Contains("2026", formatted);
    }

    [Fact]
    public void FormatFlightNumber_UsesOrdinalSuffix()
    {
        Assert.Equal("1-й", MessageFormatter.FormatFlightNumber(1));
        Assert.Equal("34-й", MessageFormatter.FormatFlightNumber(34));
    }

    [Fact]
    public void EscapeUrlForMarkdown_EscapesParentheses()
    {
        Assert.Equal("https://x.test/a%28b%29c", MessageFormatter.EscapeUrlForMarkdown("https://x.test/a(b)c"));
    }

    [Fact]
    public void FormatNotificationMessage_UsesActualMinutes()
    {
        var launch = MakeLaunch();
        var message = MessageFormatter.FormatNotificationMessage(launch, 0, 17);

        Assert.Contains("ЗАПУСК ЧЕРЕЗ 17 МИНУТ!", message);
        Assert.Contains("Falcon Heavy | Nancy Grace Roman Space Telescope", message);
    }

    [Fact]
    public void FormatLaunchMessage_DoesNotIncludeLiveStreamLink()
    {
        // Превью видео в Telegram раздувает карточку /next — ссылка только в уведомлениях
        var launch = MakeLaunch();
        launch.LiveStreamUrl = "https://youtube.test/watch?v=1";

        var message = MessageFormatter.FormatLaunchMessage(launch, 0, useRussian: true);

        Assert.DoesNotContain("Смотреть прямой эфир", message);
    }

    [Fact]
    public void FormatNotificationMessage_IncludesLiveStreamLink()
    {
        var launch = MakeLaunch();
        launch.LiveStreamUrl = "https://youtube.test/watch?v=1";

        var message = MessageFormatter.FormatNotificationMessage(launch, 0, 30);

        Assert.Contains("[Смотреть прямой эфир](https://youtube.test/watch?v=1)", message);
    }

    [Fact]
    public void TruncateFeedback_KeepsShortTextAndTrims()
    {
        Assert.Equal("привет", MessageFormatter.TruncateFeedback("  привет  "));
    }

    [Fact]
    public void TruncateFeedback_CutsLongTextWithEllipsis()
    {
        var longText = new string('a', MessageFormatter.MaxFeedbackLength + 50);

        var result = MessageFormatter.TruncateFeedback(longText);

        Assert.Equal(MessageFormatter.MaxFeedbackLength + 1, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void FormatFeedbackAdminMessage_SanitizesUserText()
    {
        var message = MessageFormatter.FormatFeedbackAdminMessage(42, "user_name", "хочу *фильтр* [по] SpaceX");

        Assert.Contains("@user name (id 42)", message); // '_' вычищен санитайзером
        Assert.Contains("хочу фильтр (по] SpaceX", message); // '[' -> '(', ']' безвреден и остаётся
        Assert.DoesNotContain("*фильтр*", message);
    }

    [Fact]
    public void FormatFeedbackAdminMessage_FallsBackToIdWhenNoUsername()
    {
        var message = MessageFormatter.FormatFeedbackAdminMessage(42, null, "текст");

        Assert.Contains("От: id 42", message);
        Assert.DoesNotContain("@", message);
    }

    [Fact]
    public void FormatLaunchMessage_ShowsBoostersAndLandingMarker()
    {
        var launch = MakeLaunch();
        launch.Boosters = new List<BoosterInfo>
        {
            new() { SerialNumber = "B1071", FlightNumber = 34, Reused = true, LandingAttempt = true },
            new() { SerialNumber = null }, // без серийника — не показывается
        };

        var message = MessageFormatter.FormatLaunchMessage(launch, 0, useRussian: true);

        Assert.Contains("♻️ Бустер B1071 (34-й полёт) 🎯", message);
    }

    [Fact]
    public void FormatLaunchMessage_FallsBackToEnglishWhenNoTranslation()
    {
        var launch = MakeLaunch();
        launch.Description = "English description";
        launch.DescriptionRu = null;

        var message = MessageFormatter.FormatLaunchMessage(launch, 0, useRussian: true);

        Assert.Contains("English description", message);
        Assert.DoesNotContain("Переведено с помощью AI", message);
    }

    private static Launch MakeLaunch() => new()
    {
        Id = "test-id",
        Name = "Falcon Heavy | Nancy Grace Roman Space Telescope",
        CountryCode = "US",
        LaunchTime = new DateTime(2026, 8, 30, 11, 26, 0, DateTimeKind.Utc),
        SpectacleRating = 5
    };
}

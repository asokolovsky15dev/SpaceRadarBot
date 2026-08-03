using SpaceRadarBot.Models;
using Xunit;

namespace SpaceRadarBot.Tests;

public class NotificationPreferenceTests
{
    [Theory]
    [InlineData(NotificationPreference.AllLaunches, 1, true)]
    [InlineData(NotificationPreference.AllLaunches, 5, true)]
    [InlineData(NotificationPreference.FiveStarsOnly, 5, true)]
    [InlineData(NotificationPreference.FiveStarsOnly, 4, false)]
    [InlineData(NotificationPreference.FourStarsAndAbove, 5, true)]
    [InlineData(NotificationPreference.FourStarsAndAbove, 4, true)]
    [InlineData(NotificationPreference.FourStarsAndAbove, 3, false)]
    [InlineData(NotificationPreference.None, 5, false)]
    [InlineData(NotificationPreference.None, 1, false)]
    public void Matches_FollowsPreferenceRules(NotificationPreference preference, int rating, bool expected)
    {
        Assert.Equal(expected, preference.Matches(rating));
    }
}

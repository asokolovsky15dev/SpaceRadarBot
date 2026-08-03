using SpaceRadarBot.Models;
using SpaceRadarBot.Services;
using Xunit;

namespace SpaceRadarBot.Tests;

public class SpectacleRatingCalculatorTests
{
    [Fact]
    public void CrewedMission_GetsFiveStars()
    {
        var launch = MakeLaunch(name: "Crew-12", description: "Astronaut crew rotation to the ISS");
        Assert.Equal(5, SpectacleRatingCalculator.Calculate(launch));
    }

    [Fact]
    public void DeepSpaceOrbit_GetsFiveStars()
    {
        var launch = MakeLaunch(orbit: "L2");
        Assert.Equal(5, SpectacleRatingCalculator.Calculate(launch));
    }

    [Fact]
    public void FalconHeavy_GetsFiveStars()
    {
        var launch = MakeLaunch(rocket: "Falcon Heavy");
        Assert.Equal(5, SpectacleRatingCalculator.Calculate(launch));
    }

    [Fact]
    public void Falcon9Starlink_GetsDefaultThreeStars()
    {
        var launch = MakeLaunch(name: "Falcon 9 Block 5 | Starlink Group 17-44", rocket: "Falcon 9");
        Assert.Equal(3, SpectacleRatingCalculator.Calculate(launch));
    }

    [Fact]
    public void Falcon9NonStarlink_GetsFourStars()
    {
        var launch = MakeLaunch(name: "Falcon 9 Block 5 | GPS III SV08", rocket: "Falcon 9");
        Assert.Equal(4, SpectacleRatingCalculator.Calculate(launch));
    }

    [Fact]
    public void MaidenFlight_UpgradesToAtLeastFour()
    {
        var launch = MakeLaunch(name: "Some Rocket | Maiden Flight", rocket: "Some Rocket");
        Assert.True(SpectacleRatingCalculator.Calculate(launch) >= 4);
    }

    [Fact]
    public void UnknownRocket_GetsDefaultThreeStars()
    {
        var launch = MakeLaunch(rocket: "Soyuz 2.1a");
        Assert.Equal(3, SpectacleRatingCalculator.Calculate(launch));
    }

    private static LaunchLibraryLaunch MakeLaunch(
        string name = "Generic | Mission",
        string? description = null,
        string? orbit = null,
        string rocket = "Generic Rocket")
    {
        return new LaunchLibraryLaunch
        {
            Id = "test",
            Name = name,
            Mission = new MissionInfo
            {
                Description = description,
                Orbit = orbit == null ? null : new OrbitInfo { Abbrev = orbit }
            },
            Rocket = new RocketInfo
            {
                Configuration = new RocketConfiguration { Name = rocket }
            }
        };
    }
}

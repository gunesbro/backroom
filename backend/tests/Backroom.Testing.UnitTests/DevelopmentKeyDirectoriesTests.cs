using AwesomeAssertions;
using Backroom.AppHost;

namespace Backroom.Testing.UnitTests;

/// <summary>The AppHost's per-instance, per-cell key directories (DevelopmentKeyDirectories, compiled from the AppHost).</summary>
public sealed class DevelopmentKeyDirectoriesTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "appdata");

    [Fact]
    public void For_ForTwoAppHosts_ReturnsDifferentDirectories() =>
        DevelopmentKeyDirectories.For(s_root, "backroom.apphost-24f7985b54", "a")
            .Should().NotBe(DevelopmentKeyDirectories.For(s_root, "backroom.apphost-bcd87c7a77", "a"));

    [Fact]
    public void For_ForTwoCellsOfOneAppHost_ReturnsDifferentDirectories() =>
        DevelopmentKeyDirectories.For(s_root, "backroom.apphost-24f7985b54", "a")
            .Should().NotBe(DevelopmentKeyDirectories.For(s_root, "backroom.apphost-24f7985b54", "b"));

    [Fact]
    public void For_Always_IsUnderTheAppHostsOwnFolderAndNotTheSharedDefault()
    {
        var directory = DevelopmentKeyDirectories.For(s_root, "backroom.apphost-24f7985b54", "a");

        directory.Should().Be(Path.Combine(s_root, "Backroom", "dev", "backroom.apphost-24f7985b54", "cells", "a"));
        directory.Should().NotBe(Path.Combine(s_root, "Backroom", "cells", "a"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a:b")]
    [InlineData("")]
    public void For_WithAnUnsafeSegment_Throws(string segment)
    {
        var appHost = () => DevelopmentKeyDirectories.For(s_root, segment, "a");
        var cell = () => DevelopmentKeyDirectories.For(s_root, "backroom.apphost-24f7985b54", segment);

        appHost.Should().Throw<ArgumentException>();
        cell.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AppHostIdFromVolumeName_ForAnAspireVolumeName_ReturnsTheApplicationAndPathHash() =>
        DevelopmentKeyDirectories.AppHostIdFromVolumeName("backroom.apphost-24f7985b54-postgres-data", "postgres-data")
            .Should().Be("backroom.apphost-24f7985b54");

    [Fact]
    public void AppHostIdFromVolumeName_WhenTheSuffixDiffers_Throws()
    {
        var parse = () => DevelopmentKeyDirectories.AppHostIdFromVolumeName("backroom.apphost-24f7985b54-valkey-data", "postgres-data");

        parse.Should().Throw<ArgumentException>();
    }
}

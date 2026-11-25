using System;
using System.IO;
using OmniScan.Net.Core;

namespace OmniScan.Net.Tests;

public sealed class UpdatesTests
{
    private const string Release = """
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/sensboston/OmniScan.NET/releases/tag/v1.2.0",
          "assets": [
            { "name": "OmniScan.exe", "size": 1000, "digest": "sha256:ABCDEF",
              "browser_download_url": "https://github.com/sensboston/OmniScan.NET/releases/download/v1.2.0/OmniScan.exe" },
            { "name": "OmniScan-lite.exe", "size": 200,
              "browser_download_url": "https://github.com/sensboston/OmniScan.NET/releases/download/v1.2.0/OmniScan-lite.exe" }
          ]
        }
        """;

    [Fact]
    public void Release_GivesTheFileOfTheSameKind()
    {
        UpdateInfo full = Assert.IsType<UpdateInfo>(Updates.Parse(Release, "OmniScan.exe"));
        UpdateInfo lite = Assert.IsType<UpdateInfo>(Updates.Parse(Release, "OmniScan-lite.exe"));

        Assert.Equal(new Version(1, 2, 0), full.Version);
        Assert.Equal(1000, full.Size);
        Assert.Equal("abcdef", full.Sha256);
        Assert.EndsWith("/OmniScan.exe", full.Download.AbsoluteUri);
        Assert.EndsWith("/OmniScan-lite.exe", lite.Download.AbsoluteUri);
        Assert.Null(lite.Sha256);
        Assert.Null(Updates.Parse(Release, "Other.exe"));
    }

    [Theory]
    [InlineData("v1.1.0", "1.1.0")]
    [InlineData("1.10.2", "1.10.2")]
    [InlineData("V2.0", "2.0.0")]
    public void Tags_AreVersions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), Updates.ParseTag(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void OtherTags_AreNot(string? tag) => Assert.Null(Updates.ParseTag(tag));

    [Fact]
    public void AssemblyVersion_ComparesByThreeParts()
    {
        Assert.Equal(new Version(1, 1, 0), Updates.Normalize(new Version(1, 1, 0, 0)));
        Assert.False(new Version(1, 1, 0) > Updates.Normalize(new Version(1, 1, 0, 0)));
    }

    [Fact]
    public void Swap_PutsTheNewFileInPlace_AndCleanUpRemovesTheOld()
    {
        using TempFolder folder = new();
        string exe = Path.Combine(folder.Path, "OmniScan.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(exe + ".new", "new");

        Updates.Swap(exe, exe + ".new");

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".new"));

        Updates.CleanUp(exe);

        Assert.False(File.Exists(exe + ".old"));
        Assert.Equal("new", File.ReadAllText(exe));
    }

    [Fact]
    public void Swap_PutsTheOldFileBack_WhenTheNewOneIsMissing()
    {
        using TempFolder folder = new();
        string exe = Path.Combine(folder.Path, "OmniScan.exe");
        File.WriteAllText(exe, "old");

        Assert.ThrowsAny<IOException>(() => Updates.Swap(exe, exe + ".new"));

        Assert.Equal("old", File.ReadAllText(exe));
    }

    [Fact]
    public void Times_RoundTrip()
    {
        DateTime t = new(2026, 11, 25, 14, 30, 5, DateTimeKind.Utc);

        Assert.Equal(t, Updates.ParseTime(Updates.FormatTime(t)));
        Assert.Null(Updates.ParseTime("never"));
    }
}

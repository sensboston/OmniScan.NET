using System.Linq;
using System.Reflection;

namespace OmniScan.Net.App;

/// <summary>Version and build time of this program, for the window title.</summary>
public static class AppInfo
{
    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    /// <summary>Major.minor.patch from the project version.</summary>
    public static string Version { get; } = Self.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Local time of the build, "yyyy-MM-dd HH:mm" (written by the project file).</summary>
    public static string BuildTime { get; } = Self.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildTime")?.Value ?? "";

    /// <summary>"OmniScan 0.9.0 — 2026-11-24 01:52".</summary>
    public static string Title { get; } = BuildTime.Length == 0 ? $"OmniScan {Version}" : $"OmniScan {Version} \u2014 {BuildTime}";
}

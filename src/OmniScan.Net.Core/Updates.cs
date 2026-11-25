using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniScan.Net.Core;

/// <summary>A newer release on GitHub: its version, the file to download and the release page.</summary>
/// <param name="Sha256">Hex digest GitHub gives for the file; null when it gives none.</param>
public sealed record UpdateInfo(Version Version, Uri Download, long Size, string? Sha256, Uri Page);

/// <summary>
/// Updates from the latest GitHub release. The program is one portable file, so updating is a swap:
/// the new file is downloaded next to the running one and checked; the running file is renamed away
/// (Windows lets a running program be renamed, not overwritten), the new one takes its name and is
/// started, and it removes the old one once the old process is gone.
/// </summary>
public static class Updates
{
    public const string Repository = "sensboston/OmniScan.NET";

    /// <summary>Started by the update: wait for the given process to end before going on.</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>Points the check to another "latest release" address (to try an update before publishing it).</summary>
    public const string FeedVariable = "OMNISCAN_UPDATE_FEED";

    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    public static Uri LatestRelease =>
        Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } feed
            ? new Uri(feed)
            : new Uri($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>An HTTP client as GitHub wants it (a User-Agent is required); downloads may take minutes.</summary>
    public static HttpClient CreateClient()
    {
        HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniScan-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Major.minor.patch of a version (the fourth part of an assembly version is not used).</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>"v1.2.0" or "1.2.0" → 1.2.0; null for anything else.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        string t = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out Version? v) ? Normalize(v) : null;
    }

    /// <summary>The release described by a GitHub "latest release" answer, with the file named <paramref name="asset"/>.</summary>
    public static UpdateInfo? Parse(string json, string asset)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (ParseTag(Text(root, "tag_name")) is not { } version || Text(root, "html_url") is not { } page)
            return null;
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (JsonElement a in assets.EnumerateArray())
        {
            if (!string.Equals(Text(a, "name"), asset, StringComparison.OrdinalIgnoreCase)
                || Text(a, "browser_download_url") is not { } url)
                continue;
            long size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long n) ? n : 0;
            string? digest = Text(a, "digest");
            string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..].ToLowerInvariant()
                : null;
            return new UpdateInfo(version, new Uri(url), size, sha, new Uri(page));
        }

        return null;
    }

    /// <summary>The latest release if it is newer than <paramref name="current"/>; null otherwise.</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, string asset, Version current, CancellationToken ct = default)
    {
        string json = await http.GetStringAsync(LatestRelease, ct);
        UpdateInfo? info = Parse(json, asset);
        return info is not null && info.Version > Normalize(current) ? info : null;
    }

    /// <summary>
    /// Downloads the new file next to <paramref name="exePath"/> and checks its size and digest;
    /// returns its path. A file that does not match is removed and an <see cref="InvalidDataException"/> thrown.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, string exePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        string target = exePath + NewSuffix;
        try
        {
            using HttpResponseMessage response = await http.GetAsync(info.Download, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? info.Size;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            int shown = -1;
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct))
            await using (FileStream file = new(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    int percent = total > 0 ? (int)(done * 100 / total) : 0;
                    if (percent != shown)
                    {
                        shown = percent;
                        progress?.Report(percent);
                    }
                }
            }

            if (info.Size > 0 && done != info.Size)
                throw new InvalidDataException($"The download has {done} bytes instead of {info.Size}.");
            string digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (info.Sha256 is not null && digest != info.Sha256)
                throw new InvalidDataException("The download does not match the release (SHA-256).");
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Puts the downloaded file in place of the running one: the running file is renamed to ".old"
    /// (allowed while it runs), the new one takes its name. On failure everything is put back.
    /// </summary>
    public static void Swap(string exePath, string newPath)
    {
        string old = exePath + OldSuffix;
        TryDelete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(newPath, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>Removes what an update left next to the program (the old file, an unfinished download).</summary>
    public static void CleanUp(string exePath)
    {
        // The old process may need a moment to let its file go.
        for (int attempt = 0; attempt < 10 && !TryDelete(exePath + OldSuffix); attempt++)
            Thread.Sleep(300);
        TryDelete(exePath + NewSuffix);
    }

    /// <summary>Whether the program's folder takes new files (a copy under Program Files does not).</summary>
    public static bool CanWriteNextTo(string exePath)
    {
        string probe = exePath + NewSuffix;
        try
        {
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>"2026-11-25T14:00:00Z" for the settings file.</summary>
    public static string FormatTime(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static DateTime? ParseTime(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
            ? t
            : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

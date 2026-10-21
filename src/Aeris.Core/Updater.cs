using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Aeris;

public record ReleaseAsset(string Name, string Url, long Size, string? Digest);

public record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Setup => Assets.FirstOrDefault(a => a.Name.EndsWith("-Setup-x64.exe", StringComparison.OrdinalIgnoreCase));
    public ReleaseAsset? Portable => Assets.FirstOrDefault(a => a.Name.StartsWith("Aeris-", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
    public ReleaseAsset? Checksums => Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
    public ReleaseAsset? LinuxDesktop => Assets.FirstOrDefault(a => a.Name.StartsWith("Aeris-", StringComparison.Ordinal)
        && a.Name.EndsWith($"-linux-{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}.tar.gz", StringComparison.Ordinal));
}

public class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Checks GitHub Releases for a newer Aeris and downloads it. This is the only code in Aeris that touches the
/// network, once at startup and when the user presses "Check for updates". Downloads are accepted only from this
/// repository's release URLs and only if their SHA-256 matches both the release's SHA256SUMS.txt and the digest
/// GitHub reports for the file.
/// </summary>
public sealed class Updater(HttpClient http)
{
    public const string Repository = "mayskiiyy/Aeris";
    public const string InstallerAppId = "{76C5DAFA-7677-4E23-A965-4B341B5E4542}_is1";
    private static readonly string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";
    private const long MaxDownload = 512L * 1024 * 1024;

    public static Version Current => typeof(Updater).Assembly.GetName().Version is { } v ? new(v.Major, v.Minor, Math.Max(0, v.Build)) : new(0, 0, 0);

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Aeris/{Current}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version? ParseTag(string? tag) =>
        tag != null && Version.TryParse(tag.TrimStart('v', 'V'), out var version) ? new(version.Major, version.Minor, Math.Max(0, version.Build)) : null;

    /// <summary>The latest published release if it is newer than <paramref name="current"/>, otherwise null. Drafts and pre-releases are never offered.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken token)
    {
        string json;
        try { json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", token); }
        catch (HttpRequestException e) { throw new UpdateException(Loc.Format("Update.Error.Network", e.Message), e); }
        catch (TaskCanceledException e) when (!token.IsCancellationRequested) { throw new UpdateException(Loc.Get("Update.Error.Timeout"), e); }
        UpdateInfo info;
        try { info = Parse(json); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { throw new UpdateException(Loc.Get("Update.Error.Response"), e); }
        return info.Version > current ? info : null;
    }

    public static UpdateInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            throw new InvalidOperationException("Not a published release.");
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag) ?? throw new InvalidOperationException($"Bad tag {tag}");
        var assets = root.GetProperty("assets").EnumerateArray().Select(a => new ReleaseAsset(
            a.GetProperty("name").GetString() ?? "",
            a.GetProperty("browser_download_url").GetString() ?? "",
            a.GetProperty("size").GetInt64(),
            a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..].ToLowerInvariant() : null)).ToList();
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : $"https://github.com/{Repository}/releases/latest";
        return new(version, tag, notes.Trim(), page, assets);
    }

    /// <summary>Hashes from a SHA256SUMS.txt file in the "hash  name" format build.ps1 writes.</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)) sums[parts[1].Trim().TrimStart('*')] = parts[0].ToLowerInvariant();
        }
        return sums;
    }

    public static bool IsTrustedUrl(string url) =>
        url.StartsWith(DownloadPrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && !url.Contains("..", StringComparison.Ordinal) && url.IndexOfAny(['%', '\\']) < 0;

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="folder"/> and returns its path. The file is kept only if
    /// its SHA-256 equals the release checksum (and GitHub's digest when present); otherwise it is deleted and this throws.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, ReleaseAsset asset, string folder, IProgress<(long Done, long Total)>? progress, CancellationToken token)
    {
        var requested = token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        token = deadline.Token;
        if (!IsTrustedUrl(asset.Url) || info.Checksums is not { } sumsAsset || !IsTrustedUrl(sumsAsset.Url)) throw new UpdateException(Loc.Get("Update.Error.Untrusted"));
        if (asset.Size <= 0 || asset.Size > MaxDownload || string.IsNullOrWhiteSpace(asset.Name)
            || asset.Name is "." or ".." || asset.Name.IndexOfAny(['/', '\\', ':']) >= 0
            || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new UpdateException(Loc.Get("Update.Error.Untrusted"));
        string expected;
        try { expected = ParseChecksums(await http.GetStringAsync(sumsAsset.Url, token)).GetValueOrDefault(asset.Name) ?? throw new UpdateException(Loc.Get("Update.Error.NoChecksum")); }
        catch (HttpRequestException e) { throw new UpdateException(Loc.Format("Update.Error.Network", e.Message), e); }
        catch (OperationCanceledException e) when (!requested.IsCancellationRequested) { throw new UpdateException(Loc.Get("Update.Error.Timeout"), e); }
        if (asset.Digest != null && asset.Digest != expected) throw new UpdateException(Loc.Get("Update.Error.Checksum"));

        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, asset.Name);
        string partial = Path.Combine(folder, $"{Guid.NewGuid():N}.part");
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[128 * 1024];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    done += read;
                    if (done > asset.Size) throw new UpdateException(Loc.Get("Update.Error.Checksum"));
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report((done, asset.Size));
                }
                if (done != asset.Size) throw new UpdateException(Loc.Get("Update.Error.Checksum"));
            }
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expected) throw new UpdateException(Loc.Get("Update.Error.Checksum"));
            File.Move(partial, target, true);
            return target;
        }
        catch (HttpRequestException e) { throw new UpdateException(Loc.Format("Update.Error.Network", e.Message), e); }
        catch (OperationCanceledException e) when (!requested.IsCancellationRequested) { throw new UpdateException(Loc.Get("Update.Error.Timeout"), e); }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public static string DownloadFolder => Directory.CreateTempSubdirectory("Aeris-update-").FullName;

    /// <summary>True when this Aeris.exe is the one the installer put in place, so the setup can replace it.</summary>
    public static bool IsInstalled(string exe)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{InstallerAppId}");
            return key?.GetValue("InstallLocation") is string location
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)), Path.GetDirectoryName(Path.GetFullPath(exe)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or ArgumentException) { return false; }
    }

    /// <summary>Starts the downloaded setup in silent mode; it closes Aeris, updates it in place and starts it again.</summary>
    public static void RunInstaller(string setup) =>
        Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1") { UseShellExecute = false });
}

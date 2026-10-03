using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OctetLedger.Core;

public sealed record UpdateRelease(
    Version Version,
    Uri ReleasePage,
    Uri PackageUri,
    Uri ChecksumUri,
    string PackageName);

public sealed class UpdateChecker(HttpClient httpClient)
{
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/Roman-Cuisset/octet-ledger/releases/latest");

    public async Task<UpdateRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd($"OctetLedger/{currentVersion}");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        GitHubRelease release;
        try
        {
            release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: cancellationToken)
                      ?? throw new InvalidDataException("GitHub returned an empty release response.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub returned malformed release metadata.", exception);
        }
        if (release.Draft || release.Prerelease) return null;

        var version = ParseVersion(release.TagName);
        if (version <= currentVersion) return null;
        var runtime = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Updates are available only for Windows x64 and ARM64.")
        };
        var packageName = $"OctetLedger-{version}-{runtime}.zip";
        var package = FindAsset(release.Assets, packageName);
        var checksum = FindAsset(release.Assets, $"{packageName}.sha256");
        if (package is null || checksum is null)
            throw new InvalidDataException($"Release {version} does not contain the expected {runtime} package and checksum.");

        var page = RequireHttpsHost(release.HtmlUrl, "github.com");
        return new UpdateRelease(
            version,
            page,
            RequireGitHubDownload(package.DownloadUrl),
            RequireGitHubDownload(checksum.DownloadUrl),
            packageName);
    }

    internal static Version ParseVersion(string? tag)
    {
        var value = tag?.StartsWith('v') == true ? tag[1..] : tag;
        return Version.TryParse(value, out var version)
            ? version
            : throw new InvalidDataException($"Release tag '{tag}' is not a valid version.");
    }

    internal static Uri RequireGitHubDownload(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsApprovedAssetUri(uri))
            throw new InvalidDataException($"Release asset URL '{value}' is not an approved GitHub HTTPS URL.");
        return uri;
    }

    public static bool IsApprovedAssetUri(Uri uri)
    {
        return uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host is "github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";
    }

    private static Uri RequireHttpsHost(string? value, string host)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Release URL '{value}' is not an approved HTTPS URL.");
        return uri;
    }

    private static GitHubAsset? FindAsset(GitHubAsset?[]? assets, string name)
    {
        if (assets is null) throw new InvalidDataException("GitHub release metadata is missing its assets.");
        GitHubAsset? selected = null;
        foreach (var asset in assets)
        {
            if (asset is null) throw new InvalidDataException("GitHub release metadata contains an empty asset.");
            if (!string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (selected is not null) throw new InvalidDataException($"GitHub release contains duplicate '{name}' assets.");
            selected = asset;
        }
        return selected;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] GitHubAsset?[]? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? DownloadUrl);
}

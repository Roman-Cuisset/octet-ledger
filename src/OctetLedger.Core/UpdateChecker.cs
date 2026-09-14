using System.Net.Http.Json;
using System.Runtime.InteropServices;
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
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: cancellationToken)
                      ?? throw new InvalidDataException("GitHub returned an empty release response.");
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
        var package = release.Assets.SingleOrDefault(asset => string.Equals(asset.Name, packageName, StringComparison.OrdinalIgnoreCase));
        var checksum = release.Assets.SingleOrDefault(asset => string.Equals(asset.Name, $"{packageName}.sha256", StringComparison.OrdinalIgnoreCase));
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

    internal static Version ParseVersion(string tag)
    {
        var value = tag.StartsWith('v') ? tag[1..] : tag;
        return Version.TryParse(value, out var version)
            ? version
            : throw new InvalidDataException($"Release tag '{tag}' is not a valid version.");
    }

    private static Uri RequireGitHubDownload(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("github.com" or "objects.githubusercontent.com"))
            throw new InvalidDataException($"Release asset URL '{uri}' is not an approved GitHub HTTPS URL.");
        return uri;
    }

    private static Uri RequireHttpsHost(string value, string host)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Release URL '{uri}' is not an approved HTTPS URL.");
        return uri;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] GitHubAsset[] Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
}

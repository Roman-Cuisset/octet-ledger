using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("invalid-json")]
    [InlineData("null-response")]
    [InlineData("missing-tag")]
    [InlineData("missing-assets")]
    [InlineData("wrong-assets-type")]
    [InlineData("null-asset")]
    [InlineData("duplicate-package")]
    [InlineData("duplicate-checksum")]
    [InlineData("invalid-page")]
    [InlineData("missing-page")]
    [InlineData("invalid-download")]
    [InlineData("missing-download")]
    public async Task MalformedReleaseMetadataIsReportedAsInvalidData(string scenario)
    {
        using var client = new HttpClient(new ReleaseHandler(CreateResponse(scenario)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateChecker(client).CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public void RelativeAssetAddressIsNotApproved()
    {
        Assert.False(UpdateChecker.IsApprovedAssetUri(new Uri("package.zip", UriKind.Relative)));
    }

    private static string CreateResponse(string scenario)
    {
        if (scenario == "invalid-json") return "{";
        if (scenario == "null-response") return "null";
        var runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var package = $"OctetLedger-9.0.0-{runtime}.zip";
        var release = JsonNode.Parse($$"""
            {
              "tag_name": "v9.0.0",
              "html_url": "https://github.com/Roman-Cuisset/octet-ledger/releases/tag/v9.0.0",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "{{package}}", "browser_download_url": "https://github.com/example/package" },
                { "name": "{{package}}.sha256", "browser_download_url": "https://github.com/example/checksum" }
              ]
            }
            """)!.AsObject();
        var assets = release["assets"]!.AsArray();
        switch (scenario)
        {
            case "missing-tag": release.Remove("tag_name"); break;
            case "missing-assets": release.Remove("assets"); break;
            case "wrong-assets-type": release["assets"] = "not an array"; break;
            case "null-asset": assets.Insert(0, null); break;
            case "duplicate-package": assets.Add(assets[0]!.DeepClone()); break;
            case "duplicate-checksum": assets.Add(assets[1]!.DeepClone()); break;
            case "invalid-page": release["html_url"] = "relative/page"; break;
            case "missing-page": release.Remove("html_url"); break;
            case "invalid-download": assets[0]!["browser_download_url"] = "relative/package"; break;
            case "missing-download": assets[0]!.AsObject().Remove("browser_download_url"); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return release.ToJsonString();
    }

    private sealed class ReleaseHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
    }
}

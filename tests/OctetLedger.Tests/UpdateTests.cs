using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OctetLedger.Cli;
using OctetLedger.Core;

namespace OctetLedger.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("v0.4.2", 0, 4, 2)]
    [InlineData("1.10.0", 1, 10, 0)]
    public void ReleaseVersionParsesSemanticVersion(string tag, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), UpdateChecker.ParseVersion(tag));
    }

    [Fact]
    public async Task NewReleaseSelectsPackageForCurrentArchitecture()
    {
        var runtime = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "win-arm64"
            : "win-x64";
        var package = $"OctetLedger-9.0.0-{runtime}.zip";
        using var client = new HttpClient(new StubHandler($$"""
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
            """));

        var release = await new UpdateChecker(client).CheckAsync(new Version(0, 4, 1));

        Assert.NotNull(release);
        Assert.Equal(new Version(9, 0, 0), release.Version);
        Assert.Equal(package, release.PackageName);
    }

    [Fact]
    public async Task CurrentReleaseDoesNotOfferAnUpdate()
    {
        using var client = new HttpClient(new StubHandler("""
            {
              "tag_name": "v0.4.1",
              "html_url": "https://github.com/Roman-Cuisset/octet-ledger/releases/tag/v0.4.1",
              "draft": false,
              "prerelease": false,
              "assets": []
            }
            """));

        Assert.Null(await new UpdateChecker(client).CheckAsync(new Version(0, 4, 1)));
    }

    [Fact]
    public void InvalidChecksumIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "package");
            Assert.Throws<InvalidDataException>(() => UpdateInstaller.VerifyChecksum(path, new string('0', 64)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ValidChecksumIsAccepted()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "package");
            var checksum = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            UpdateInstaller.VerifyChecksum(path, $"{checksum}  package.zip");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ArchiveTraversalIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(root, "bad.zip");
        var extractionPath = Path.Combine(root, "extract");
        Directory.CreateDirectory(root);
        try
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../outside.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("bad");
            }

            Assert.Throws<InvalidDataException>(() => UpdateInstaller.ExtractArchive(archivePath, extractionPath));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}

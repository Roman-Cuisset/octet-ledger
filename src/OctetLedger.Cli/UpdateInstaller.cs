using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OctetLedger.Core;

namespace OctetLedger.Cli;

internal sealed partial class UpdateInstaller
{
    internal const long MaximumDownloadBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);
    private readonly HttpClient httpClient;
    private readonly TimeSpan downloadTimeout;
    private readonly long maximumDownloadBytes;

    public UpdateInstaller(HttpClient httpClient)
        : this(httpClient, DownloadTimeout, MaximumDownloadBytes) { }

    internal UpdateInstaller(HttpClient httpClient, TimeSpan downloadTimeout, long maximumDownloadBytes)
    {
        this.httpClient = httpClient;
        this.downloadTimeout = downloadTimeout;
        this.maximumDownloadBytes = maximumDownloadBytes;
    }
    public async Task StartInstallAsync(UpdateRelease release, CancellationToken cancellationToken = default)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"octetledger-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var archivePath = Path.Combine(temporaryRoot, release.PackageName);
            var checksumPath = $"{archivePath}.sha256";
            await DownloadAsync(release.PackageUri, archivePath, cancellationToken);
            await DownloadAsync(release.ChecksumUri, checksumPath, cancellationToken);
            VerifyChecksum(archivePath, await File.ReadAllTextAsync(checksumPath, cancellationToken));

            var extractionPath = Path.Combine(temporaryRoot, "package");
            ExtractArchive(archivePath, extractionPath);
            var executable = RequireSingleFile(extractionPath, "octetledger.exe");
            var installScript = RequireSingleFile(extractionPath, "install.ps1");
            await VerifyExecutableVersionAsync(executable, release.Version, cancellationToken);

            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(installScript);
            startInfo.ArgumentList.Add("-Source");
            startInfo.ArgumentList.Add(executable);
            startInfo.ArgumentList.Add("-WaitForProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-CleanupDirectory");
            startInfo.ArgumentList.Add(temporaryRoot);
            Process.Start(startInfo)?.Dispose();
        }
        catch
        {
            TryDeleteDirectory(temporaryRoot);
            throw;
        }
    }

    internal static void VerifyChecksum(string archivePath, string checksumText)
    {
        var match = ChecksumRegex().Match(checksumText.Trim());
        if (!match.Success) throw new InvalidDataException("The release checksum file is invalid.");
        using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
    }

    internal static void ExtractArchive(string archivePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var destinationRoot = Path.GetFullPath(destinationPath) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(destinationPath, entry.FullName));
            if (!destination.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry '{entry.FullName}' escapes the extraction directory.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    internal async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(downloadTimeout);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (!UpdateChecker.IsApprovedAssetUri(finalUri))
            throw new InvalidDataException($"GitHub redirected the update to an unapproved URL '{finalUri}'.");
        if (response.Content.Headers.ContentLength is long length && length > maximumDownloadBytes)
            throw new InvalidDataException($"The update download exceeds the {maximumDownloadBytes} byte safety limit.");

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            total += read;
            if (total > maximumDownloadBytes)
                throw new InvalidDataException($"The update download exceeds the {maximumDownloadBytes} byte safety limit.");
            await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
    }

    private static string RequireSingleFile(string root, string fileName)
    {
        var matches = Directory.GetFiles(root, fileName, SearchOption.AllDirectories);
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"The update package must contain exactly one {fileName}.");
    }

    private static async Task VerifyExecutableVersionAsync(string executable, Version expected, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("The downloaded OctetLedger executable could not be started.");
        var (output, _) = await ReadProcessOutputAsync(
            process,
            TimeSpan.FromSeconds(OctetLedgerDefaults.ExternalProcessTimeoutSeconds),
            cancellationToken);
        if (process.ExitCode != 0 || !string.Equals(output.Trim(), $"OctetLedger {expected}", StringComparison.Ordinal))
            throw new InvalidDataException($"The downloaded executable does not match release {expected}.");
    }

    internal static async Task<(string Output, string Error)> ReadProcessOutputAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return (await output, await error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException("The downloaded executable did not complete version validation.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex("^([0-9a-fA-F]{64})(?:\\s+\\*?.+)?$")]
    private static partial Regex ChecksumRegex();
}

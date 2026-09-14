using System.Globalization;
using OctetLedger.Core;

namespace OctetLedger.Cli;

internal static class UpdateCommand
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    public static async Task<int> RunAsync(string[] arguments, Version currentVersion)
    {
        if (arguments.Length > 1)
        {
            Console.Error.WriteLine("Usage: octetledger update [check|install|rollback|status|enable|disable]");
            return 2;
        }

        var action = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "check";
        try
        {
            switch (action)
            {
                case "check":
                    return await CheckAsync(currentVersion);
                case "install":
                    return await InstallAsync(currentVersion);
                case "rollback":
                    return StartRollback();
                case "status":
                    ShowStatus(currentVersion);
                    return 0;
                case "enable":
                    SaveUpdatePreference(enabled: true);
                    Console.WriteLine("Automatic daily update checks are enabled.");
                    return 0;
                case "disable":
                    SaveUpdatePreference(enabled: false);
                    Console.WriteLine("Automatic update checks are disabled.");
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: octetledger update [check|install|rollback|status|enable|disable]");
                    return 2;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or IOException or UnauthorizedAccessException or
                                          InvalidOperationException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Update failed: {exception.Message}");
            return 1;
        }
    }

    public static async Task MaybeNotifyAsync(Version currentVersion)
    {
        var settings = OctetLedgerSettings.Load();
        if (!settings.CheckForUpdates ||
            settings.LastUpdateCheckUtc is { } checkedAt && DateTimeOffset.UtcNow - checkedAt < AutomaticCheckInterval)
            return;

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var attemptedAt = DateTimeOffset.UtcNow;
            settings = settings with { LastUpdateCheckUtc = attemptedAt };
            settings.Save();
            using var client = CreateClient();
            var release = await new UpdateChecker(client).CheckAsync(currentVersion, cancellation.Token);
            (settings with
            {
                LastSuccessfulUpdateCheckUtc = DateTimeOffset.UtcNow,
                LatestKnownVersion = release?.Version.ToString()
            }).Save();
            if (release is not null)
                Console.Error.WriteLine($"Update available: OctetLedger {release.Version}. Run 'octetledger update install'.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                          InvalidDataException or IOException or UnauthorizedAccessException or
                                          PlatformNotSupportedException)
        {
            // Automatic checks never make an otherwise local command fail.
        }
    }

    private static async Task<int> CheckAsync(Version currentVersion)
    {
        using var client = CreateClient();
        var release = await new UpdateChecker(client).CheckAsync(currentVersion);
        var settings = OctetLedgerSettings.Load();
        (settings with
        {
            LastUpdateCheckUtc = DateTimeOffset.UtcNow,
            LastSuccessfulUpdateCheckUtc = DateTimeOffset.UtcNow,
            LatestKnownVersion = release?.Version.ToString()
        }).Save();
        Console.WriteLine($"Current version: {currentVersion}");
        if (release is null)
        {
            Console.WriteLine($"OctetLedger {currentVersion} is up to date.");
            return 0;
        }
        Console.WriteLine($"Latest version:  {release.Version}");
        Console.WriteLine($"Release: {release.ReleasePage}");
        Console.WriteLine("Run 'octetledger update install' to install it.");
        return 0;
    }

    private static async Task<int> InstallAsync(Version currentVersion)
    {
        using var client = CreateClient();
        var release = await new UpdateChecker(client).CheckAsync(currentVersion);
        if (release is null)
        {
            Console.WriteLine($"OctetLedger {currentVersion} is up to date.");
            return 0;
        }
        Console.WriteLine($"Downloading and verifying OctetLedger {release.Version}...");
        await new UpdateInstaller(client).StartInstallAsync(release);
        Console.WriteLine("The verified update installer was started. This process will now exit so the executable can be replaced.");
        return 0;
    }

    private static void ShowStatus(Version currentVersion)
    {
        var settings = OctetLedgerSettings.Load();
        Console.WriteLine($"Current version:        {currentVersion}");
        Console.WriteLine($"Automatic checks:       {(settings.CheckForUpdates ? "enabled" : "disabled")}");
        Console.WriteLine($"Last check attempt:     {settings.LastUpdateCheckUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "never"}");
        Console.WriteLine($"Last successful check:  {settings.LastSuccessfulUpdateCheckUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "never"}");
        Console.WriteLine($"Latest known version:   {settings.LatestKnownVersion ?? "unknown"}");
    }

    private static int StartRollback()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("The current executable path is unavailable.");
        var directory = Path.GetDirectoryName(executable)!;
        var script = Path.Combine(directory, "rollback.ps1");
        var previous = Path.Combine(directory, "octetledger.rollback.exe");
        if (!File.Exists(script) || !File.Exists(previous))
            throw new InvalidOperationException("No previous OctetLedger version is available for rollback.");
        var startInfo = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-WaitForProcessId", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) })
            startInfo.ArgumentList.Add(argument);
        System.Diagnostics.Process.Start(startInfo)?.Dispose();
        Console.WriteLine("Rollback started. This process will exit so the executable can be replaced.");
        return 0;
    }

    private static void SaveUpdatePreference(bool enabled)
    {
        var settings = OctetLedgerSettings.Load();
        (settings with { CheckForUpdates = enabled }).Save();
    }

    private static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(10) };
}

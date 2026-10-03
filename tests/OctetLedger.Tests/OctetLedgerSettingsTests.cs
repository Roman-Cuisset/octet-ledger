using OctetLedger.Core;

namespace OctetLedger.Tests;

public class OctetLedgerSettingsTests
{
    [Theory]
    [InlineData("{\"PreferredInterfaceId\":\"important\",")]
    [InlineData("null")]
    public void InvalidSettingsCannotBeOverwrittenByAMutation(string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(path, content);
            Assert.Throws<InvalidDataException>(() => OctetLedgerSettings.Update(path,
                current => current with { AutomaticBackups = true }));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentMutationsPreserveIndependentSettingsAndEveryIncrement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"octetledger-tests-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            OctetLedgerSettings.Update(path, _ => new OctetLedgerSettings(MonthlyBudgetBytes: 100, AutomaticBackups: true));
            var mutations = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
                OctetLedgerSettings.Update(path, current => current with
                {
                    MonthlyBudgetBytes = current.MonthlyBudgetBytes + 1
                }))).ToArray();
            var preference = Task.Run(() => OctetLedgerSettings.Update(path, current => current with
            {
                PreferredInterfaceId = "wifi"
            }));

            await Task.WhenAll(mutations.Append(preference));

            var settings = OctetLedgerSettings.Load(path);
            Assert.Equal(120, settings.MonthlyBudgetBytes);
            Assert.Equal("wifi", settings.PreferredInterfaceId);
            Assert.True(settings.AutomaticBackups);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

using OpenVpnPilot.App.Services.Library;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// Which backups are kept, and how a machine is named beside the shared file.
/// </summary>
public sealed class SharedLibraryBackupsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retention_KeepsTheRecentOnesAndOneADayForAMonth()
    {
        List<(string Path, DateTime WrittenUtc)> backups = [];

        // Thirty versions today, one every ten minutes.
        for (int index = 0; index < 30; index++)
        {
            backups.Add(($"today-{index}", Now.UtcDateTime.AddMinutes(-10 * index)));
        }

        // Three a day for the forty days before.
        for (int day = 1; day <= 40; day++)
        {
            for (int index = 0; index < 3; index++)
            {
                backups.Add(($"day{day}-{index}", Now.UtcDateTime.Date.AddDays(-day).AddHours(8 + index)));
            }
        }

        IReadOnlyList<string> expendable = SharedLibraryBackups.Expendable(backups, Now);
        HashSet<string> kept = [.. backups.Select(backup => backup.Path).Except(expendable)];

        Assert.Equal(SharedLibraryBackups.RecentKept, kept.Count(path => path.StartsWith("today-", StringComparison.Ordinal)));
        Assert.Contains("day1-2", kept);
        Assert.DoesNotContain("day1-1", kept);
        Assert.Contains("day29-2", kept);
        Assert.DoesNotContain("day35-2", kept);
    }

    [Theory]
    [InlineData("alex", "WORKSTATION-7", "alex@WORKSTATION-7")]
    [InlineData("ops.team_1", "host.example", "ops.team_1@host.example")]
    public void AnOrdinaryName_IsUsedAsItIs(string user, string machine, string expected) =>
        Assert.Equal(expected, new SharedLibraryMember { User = user, Machine = machine }.FileName);

    [Fact]
    public void ANameThatIsNotSafe_IsEscapedAndCannotCollide()
    {
        SharedLibraryMember spaced = new() { User = "Jo Doe", Machine = "desk" };
        SharedLibraryMember underscored = new() { User = "Jo_Doe", Machine = "desk" };
        SharedLibraryMember hostile = new() { User = "../../etc", Machine = "a/b\\c:*?\"<>|" };

        Assert.StartsWith("Jo_Doe@desk-", spaced.FileName, StringComparison.Ordinal);
        Assert.Equal("Jo_Doe@desk", underscored.FileName);
        Assert.NotEqual(spaced.FileName, underscored.FileName);

        Assert.DoesNotContain('/', hostile.FileName);
        Assert.DoesNotContain('\\', hostile.FileName);
        Assert.DoesNotContain("..", hostile.FileName, StringComparison.Ordinal);
        Assert.Equal(hostile.FileName, Path.GetFileName(hostile.FileName));
    }

    [Fact]
    public void AnEmptyName_StillNamesAFile() =>
        Assert.StartsWith("unknown@unknown", new SharedLibraryMember { User = "", Machine = "." }.FileName, StringComparison.Ordinal);
}

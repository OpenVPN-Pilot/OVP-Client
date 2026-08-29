using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Data.Entities;

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// The connection history, with filters by profile and period and a comma separated export.
/// </summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly ISessionStore sessions;
    private readonly IProfileStore profiles;
    private readonly ILocalizer localizer;

    public HistoryViewModel(ISessionStore sessions, IProfileStore profiles, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(localizer);

        this.sessions = sessions;
        this.profiles = profiles;
        this.localizer = localizer;

        Periods =
        [
            new HistoryPeriod(HistoryRange.Last7Days, localizer["history.last7"]),
            new HistoryPeriod(HistoryRange.Last30Days, localizer["history.last30"]),
            new HistoryPeriod(HistoryRange.Last90Days, localizer["history.last90"]),
            new HistoryPeriod(HistoryRange.Everything, localizer["history.everything"]),
        ];

        SelectedPeriod = Periods[1];
    }

    public ObservableCollection<SessionRowViewModel> Rows { get; } = [];

    public ObservableCollection<HistoryProfileChoice> ProfileChoices { get; } = [];

    public ObservableCollection<HistoryPeriod> Periods { get; }

    [ObservableProperty]
    public partial HistoryProfileChoice? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial HistoryPeriod? SelectedPeriod { get; set; }

    [ObservableProperty]
    public partial string TotalsDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool HasRows => Rows.Count > 0;

    public string EmptyText => localizer["history.empty"];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (ProfileChoices.Count == 0)
        {
            ProfileChoices.Add(new HistoryProfileChoice(null, localizer["history.allProfiles"]));

            foreach (Profile profile in await profiles.GetProfilesAsync(cancellationToken))
            {
                ProfileChoices.Add(new HistoryProfileChoice(profile.Id, profile.Name));
            }

            SelectedProfile = ProfileChoices[0];
        }

        await RefreshAsync(cancellationToken);
    }

    partial void OnSelectedProfileChanged(HistoryProfileChoice? value) => _ = RefreshAsync();

    partial void OnSelectedPeriodChanged(HistoryPeriod? value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;

        try
        {
            SessionQuery query = BuildQuery();

            IReadOnlyList<SessionRecord> records = await sessions.GetHistoryAsync(query, cancellationToken);
            SessionTotals totals = await sessions.GetTotalsAsync(query, cancellationToken);

            Rows.Clear();
            foreach (SessionRecord record in records)
            {
                Rows.Add(new SessionRowViewModel(record, localizer));
            }

            TotalsDisplay = localizer.Translate(
                "history.totals",
                totals.Count,
                FormatDuration(totals.Duration),
                FormatBytes(totals.BytesReceived),
                FormatBytes(totals.BytesSent));

            OnPropertyChanged(nameof(HasRows));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Writes the current selection to a comma separated file.
    /// </summary>
    /// <remarks>
    /// Values are quoted and embedded quotes doubled, because a profile name is free text and a
    /// stray comma would otherwise shift every later column.
    /// </remarks>
    public async Task<int> ExportCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IReadOnlyList<SessionRecord> records = await sessions.GetHistoryAsync(BuildQuery(), cancellationToken);

        StringBuilder builder = new();
        builder.AppendLine("Profile,Started,Ended,DurationSeconds,BytesReceived,BytesSent,Server,Port,Reason,Detail");

        foreach (SessionRecord record in records)
        {
            builder
                .Append(Quote(record.ProfileName)).Append(',')
                .Append(record.StartedAt.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(record.EndedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append((record.Duration?.TotalSeconds ?? 0).ToString("F0", CultureInfo.InvariantCulture)).Append(',')
                .Append(record.BytesReceived.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(record.BytesSent.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Quote(record.ServerAddress ?? string.Empty)).Append(',')
                .Append(record.ServerPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(record.EndReason.ToString()).Append(',')
                .Append(Quote(record.EndDetail ?? string.Empty))
                .AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false), cancellationToken);

        StatusMessage = localizer.Translate("history.exported", records.Count, path);
        return records.Count;
    }

    private SessionQuery BuildQuery()
    {
        DateTimeOffset? from = SelectedPeriod?.Range switch
        {
            HistoryRange.Last7Days => DateTimeOffset.UtcNow.AddDays(-7),
            HistoryRange.Last30Days => DateTimeOffset.UtcNow.AddDays(-30),
            HistoryRange.Last90Days => DateTimeOffset.UtcNow.AddDays(-90),
            _ => null,
        };

        return new SessionQuery(SelectedProfile?.ProfileId, from);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static string FormatDuration(TimeSpan value) => value.TotalDays >= 1
        ? value.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}

/// <summary>
/// One row of the history list.
/// </summary>
public sealed class SessionRowViewModel
{
    private readonly SessionRecord record;
    private readonly ILocalizer localizer;

    public SessionRowViewModel(SessionRecord record, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(localizer);

        this.record = record;
        this.localizer = localizer;
    }

    public string ProfileName => record.ProfileName;

    public string StartedDisplay =>
        record.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string DurationDisplay => record.Duration is { } duration
        ? HistoryViewModel.FormatDuration(duration)
        : "-";

    public string TransferDisplay =>
        $"{HistoryViewModel.FormatBytes(record.BytesReceived)} / {HistoryViewModel.FormatBytes(record.BytesSent)}";

    public string ServerDisplay => record.ServerAddress is { Length: > 0 }
        ? $"{record.ServerAddress}:{record.ServerPort}"
        : "-";

    public string ReasonDisplay => localizer["history.reason." + record.EndReason];

    public bool IsFailure => record.EndReason
        is SessionEndReason.AuthenticationFailed
        or SessionEndReason.ConnectionLost
        or SessionEndReason.Error;

    public string? Detail => record.EndDetail;

    public bool HasDetail => record.EndDetail is { Length: > 0 };
}

/// <summary>
/// One entry in the profile filter. A null identifier means every profile.
/// </summary>
public sealed record HistoryProfileChoice(Guid? ProfileId, string Name);

/// <summary>
/// One entry in the period filter.
/// </summary>
public sealed record HistoryPeriod(HistoryRange Range, string Name);

public enum HistoryRange
{
    Last7Days,
    Last30Days,
    Last90Days,
    Everything,
}

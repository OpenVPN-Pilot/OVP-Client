using System.ComponentModel;
using OpenVpnPilot.App.Services;
using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Data.Import;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// What the import screen shows once a package has been chosen.
/// </summary>
/// <remarks>
/// The passphrase field appears on a state derived from the chosen path. Choosing a package once
/// announced only that a package was chosen, so a screen already open kept showing an empty list
/// with no field to type the passphrase into.
/// </remarks>
public sealed class ImportViewModelTests
{
    [Fact]
    public async Task ChoosingAPackage_AnnouncesThePassphraseStepToAnOpenScreen()
    {
        ImportViewModel model = new(new UnusedImporter(), new FakeProfileStore(), new UnusedPackages(), new StubLocalizer());
        List<string?> announced = [];
        ((INotifyPropertyChanged)model).PropertyChanged += (_, args) => announced.Add(args.PropertyName);

        await model.ExamineAsync(["C:/example/team-set.ovppkg"]);

        Assert.True(model.IsPackageLocked);
        Assert.False(model.IsPackageOpen);
        Assert.Equal("import.open", model.CommitLabel);
        Assert.Contains(nameof(ImportViewModel.IsPackageLocked), announced);
        Assert.Contains(nameof(ImportViewModel.IsPackageOpen), announced);
        Assert.Contains(nameof(ImportViewModel.CommitLabel), announced);
    }

    private sealed class UnusedImporter : IProfileImportService
    {
        public Task<ImportSelection> ExpandAsync(IEnumerable<string> paths, bool includeSubfolders = true, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A package is not expanded.");

        public Task<IReadOnlyList<ImportCandidate>> PrepareAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("A package is not prepared.");

        public Task<int> CommitAsync(IReadOnlyList<ImportCandidate> candidates, IReadOnlyList<string> tagNames, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nothing is committed here.");
    }

    private sealed class UnusedPackages : IProfilePackageWriter
    {
        public Task<PackageWriteResult> WriteAsync(string path, PackageExportRequest request, string passphrase, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nothing is written here.");

        public Task<OpenedPackage> OpenAsync(string path, string? passphrase, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nothing is opened here.");

        public Task<PackageImportResult> ApplyAsync(OpenedPackage package, PackageImportChoice choice, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Nothing is applied here.");

        public Task<IReadOnlyDictionary<Guid, int>> CountCredentialsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>());
    }
}

namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// One copy of the shared file in the list to restore from.
/// </summary>
/// <param name="CanRestore">False for a copy the passphrase stored now does not open.</param>
public sealed record LibraryBackupViewModel(string Path, string When, string Source, string Contents, bool CanRestore);

/// <summary>
/// One machine in the list of those using the library.
/// </summary>
public sealed record LibraryMemberViewModel(string Name, string Detail, bool HasLeft);

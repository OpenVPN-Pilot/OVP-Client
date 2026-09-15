namespace OpenVpnPilot.App.ViewModels;

/// <summary>
/// One line in the record of what the shared library did.
/// </summary>
public sealed record LibraryActivityViewModel(string Time, string Text, bool IsProblem);

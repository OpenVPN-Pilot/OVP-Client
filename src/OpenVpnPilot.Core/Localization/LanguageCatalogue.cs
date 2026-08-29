namespace OpenVpnPilot.Core.Localization;

/// <summary>
/// Every string of one language, keyed by the identifier the views use.
/// </summary>
public sealed class LanguageCatalogue
{
    public LanguageCatalogue(LanguageDescriptor descriptor, IReadOnlyDictionary<string, string> strings)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(strings);

        Descriptor = descriptor;
        Strings = strings;
    }

    public LanguageDescriptor Descriptor { get; }

    public IReadOnlyDictionary<string, string> Strings { get; }

    public string Code => Descriptor.Code;
}

/// <summary>
/// Supplies the catalogues the application knows about.
/// </summary>
/// <remarks>
/// Kept behind an interface so the source can be a directory of files, an embedded resource or a
/// fixture in a test, without the manager caring which.
/// </remarks>
public interface ILanguageCatalogueSource
{
    /// <summary>
    /// Reads every catalogue that is currently available.
    /// </summary>
    /// <remarks>
    /// Called again on reload, so it must reflect files added since the last call rather than a
    /// snapshot taken once.
    /// </remarks>
    public IReadOnlyList<LanguageCatalogue> Load();
}

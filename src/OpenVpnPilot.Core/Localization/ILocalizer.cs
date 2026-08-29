namespace OpenVpnPilot.Core.Localization;

/// <summary>
/// Resolves the text shown to the user for a given key.
/// </summary>
/// <remarks>
/// The language can change while the application runs, so every consumer that caches a resolved
/// string has to renew it when <see cref="LanguageChanged"/> is raised. A missing key resolves to
/// the key itself rather than to an empty string, because a visible key is a defect that reports
/// itself instead of a label that silently disappears.
/// </remarks>
public interface ILocalizer
{
    /// <summary>
    /// Code of the language currently in use, for example en or de.
    /// </summary>
    public string CurrentLanguage { get; }

    /// <summary>
    /// Every language that was found, ordered by name.
    /// </summary>
    public IReadOnlyList<LanguageDescriptor> AvailableLanguages { get; }

    /// <summary>
    /// Raised after the active language changed, on the thread that changed it.
    /// </summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Every key that resolves to text, across the active language and the fallback.
    /// </summary>
    /// <remarks>
    /// Exists so the presentation layer can publish the whole catalogue in one go rather than
    /// resolving one key at a time, which is what makes a live language switch possible.
    /// </remarks>
    public IReadOnlyCollection<string> Keys { get; }

    /// <summary>
    /// The text for a key, or the key itself when it is not defined in any catalogue.
    /// </summary>
    public string this[string key] { get; }

    /// <summary>
    /// The text for a key with its placeholders filled in.
    /// </summary>
    public string Translate(string key, params object?[] arguments);

    /// <summary>
    /// Switches the active language.
    /// </summary>
    /// <returns>False when no catalogue carries that code, in which case nothing changed.</returns>
    public bool TrySetLanguage(string languageCode);

    /// <summary>
    /// Re-reads the catalogues, so a language file added while the application runs is picked up.
    /// </summary>
    public void Reload();
}

/// <summary>
/// Identifies one language in the language picker.
/// </summary>
/// <param name="Code">A short code such as en or de.</param>
/// <param name="NativeName">The name in the language itself, which is what the picker shows.</param>
/// <param name="EnglishName">The English name, for logs and support requests.</param>
public sealed record LanguageDescriptor(string Code, string NativeName, string EnglishName);

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenVpnPilot.Core.Localization;

/// <summary>
/// Resolves display text from the catalogues supplied by a source.
/// </summary>
/// <remarks>
/// Lookups fall back in a fixed order: the active language, then English, then the key itself. A
/// translator who has covered half a file therefore ships a usable application rather than one with
/// empty labels, and a key that exists nowhere is visible instead of silent.
/// </remarks>
public sealed class LocalizationManager : ILocalizer
{
    /// <summary>
    /// The language every catalogue is measured against and the one used when a key is missing.
    /// </summary>
    public const string FallbackLanguage = "en";

    private readonly ILanguageCatalogueSource source;
    private readonly ILogger<LocalizationManager> logger;
    private readonly Lock gate = new();

    private Dictionary<string, LanguageCatalogue> catalogues = new(StringComparer.OrdinalIgnoreCase);
    private LanguageCatalogue? active;
    private LanguageCatalogue? fallback;
    private string currentLanguage = FallbackLanguage;

    public LocalizationManager(
        ILanguageCatalogueSource source,
        ILogger<LocalizationManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        this.source = source;
        this.logger = logger ?? NullLogger<LocalizationManager>.Instance;

        LoadCatalogues();
    }

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage
    {
        get
        {
            lock (gate)
            {
                return currentLanguage;
            }
        }
    }

    public IReadOnlyList<LanguageDescriptor> AvailableLanguages
    {
        get
        {
            lock (gate)
            {
                return catalogues.Values
                    .Select(catalogue => catalogue.Descriptor)
                    .OrderBy(descriptor => descriptor.NativeName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            lock (gate)
            {
                HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

                foreach (string key in fallback?.Strings.Keys ?? [])
                {
                    keys.Add(key);
                }

                foreach (string key in active?.Strings.Keys ?? [])
                {
                    keys.Add(key);
                }

                return keys;
            }
        }
    }

    public string this[string key] => Resolve(key);

    public string Translate(string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string template = Resolve(key);

        if (arguments.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException exception)
        {
            // A translator's stray brace must not take a screen down, so the template is shown as is.
            LocalizationLog.FormatFailed(logger, key, CurrentLanguage, exception);
            return template;
        }
    }

    public bool TrySetLanguage(string languageCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        lock (gate)
        {
            if (!catalogues.TryGetValue(languageCode, out LanguageCatalogue? catalogue))
            {
                return false;
            }

            if (string.Equals(currentLanguage, catalogue.Code, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            active = catalogue;
            currentLanguage = catalogue.Code;
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Reload()
    {
        LoadCatalogues();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Picks the best match for a system language such as de-DE, falling back to the base language
    /// and finally to English.
    /// </summary>
    public string ResolveBestMatch(string requested)
    {
        ArgumentNullException.ThrowIfNull(requested);

        lock (gate)
        {
            if (catalogues.TryGetValue(requested, out LanguageCatalogue? exact))
            {
                return exact.Code;
            }

            int separator = requested.IndexOfAny(['-', '_']);
            if (separator > 0)
            {
                string baseLanguage = requested[..separator];
                if (catalogues.TryGetValue(baseLanguage, out LanguageCatalogue? match))
                {
                    return match.Code;
                }
            }

            return FallbackLanguage;
        }
    }

    private string Resolve(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        LanguageCatalogue? current;
        LanguageCatalogue? english;
        string language;

        lock (gate)
        {
            current = active;
            english = fallback;
            language = currentLanguage;
        }

        if (current is not null && current.Strings.TryGetValue(key, out string? text))
        {
            return text;
        }

        if (english is not null && english.Strings.TryGetValue(key, out string? fallbackText))
        {
            LocalizationLog.KeyMissing(logger, key, language);
            return fallbackText;
        }

        LocalizationLog.KeyMissing(logger, key, language);
        return key;
    }

    private void LoadCatalogues()
    {
        IReadOnlyList<LanguageCatalogue> loaded = source.Load();

        lock (gate)
        {
            catalogues = loaded.ToDictionary(
                catalogue => catalogue.Code,
                catalogue => catalogue,
                StringComparer.OrdinalIgnoreCase);

            if (catalogues.Count == 0)
            {
                LocalizationLog.NoCatalogues(logger);
            }

            catalogues.TryGetValue(FallbackLanguage, out fallback);

            // Keep the selected language across a reload when it is still available.
            if (!catalogues.TryGetValue(currentLanguage, out active))
            {
                active = fallback ?? catalogues.Values.FirstOrDefault();
                currentLanguage = active?.Code ?? FallbackLanguage;
            }
        }
    }
}

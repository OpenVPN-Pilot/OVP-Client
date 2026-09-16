using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenVpnPilot.Core.Localization;

/// <summary>
/// Reads language catalogues from JSON files, one file per language.
/// </summary>
/// <remarks>
/// Adding a language means dropping a file into one of the directories; nothing is compiled in. The
/// directories are consulted in order and a later one wins, so a file the user places beside their
/// own data can correct or extend the one that ships with the application without replacing it.
///
/// A file that cannot be read is reported and skipped. Refusing to start because one translation is
/// malformed would be a worse outcome than running with the fallback language for those keys.
///
/// A key may carry a platform suffix, such as <c>environment.hint@macos</c>. On that platform it
/// replaces the key without the suffix, and everywhere else it is dropped. Wording that names a part
/// of one operating system therefore sits beside the neutral key in the same file, instead of in a
/// view that asks which system it runs on. Every consumer sees the resolved key only, which is what
/// lets the markup keep asking for <c>environment.hint</c> on every platform.
/// </remarks>
public sealed class JsonLanguageCatalogueSource : ILanguageCatalogueSource
{
    /// <summary>
    /// Separates a key from the platform its wording belongs to.
    /// </summary>
    public const char PlatformSeparator = '@';

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IReadOnlyList<string> directories;
    private readonly ILogger<JsonLanguageCatalogueSource> logger;
    private readonly string? platform;

    /// <param name="directories">
    /// Searched in order. A language present in more than one is taken from the last, and its
    /// strings are layered on top of the earlier ones so a partial override stays valid.
    /// </param>
    /// <param name="platform">
    /// The platform whose suffixed keys replace the neutral ones, such as <c>macos</c>. Null keeps
    /// the neutral wording throughout.
    /// </param>
    public JsonLanguageCatalogueSource(
        IEnumerable<string> directories,
        ILogger<JsonLanguageCatalogueSource>? logger = null,
        string? platform = null)
    {
        ArgumentNullException.ThrowIfNull(directories);

        this.directories = directories.ToList();
        this.logger = logger ?? NullLogger<JsonLanguageCatalogueSource>.Instance;
        this.platform = platform;
    }

    /// <summary>
    /// Splits a key into the key it stands for and the platform it is meant for.
    /// </summary>
    /// <returns>False for a key that carries no platform.</returns>
    public static bool TrySplitPlatformKey(string key, out string neutralKey, out string platform)
    {
        ArgumentNullException.ThrowIfNull(key);

        int separator = key.LastIndexOf(PlatformSeparator);

        // A separator inside an earlier segment is part of that segment's name, not a platform.
        if (separator <= 0 || separator == key.Length - 1 || key.IndexOf('.', separator) >= 0)
        {
            neutralKey = key;
            platform = string.Empty;
            return false;
        }

        neutralKey = key[..separator];
        platform = key[(separator + 1)..];
        return true;
    }

    public IReadOnlyList<LanguageCatalogue> Load()
    {
        Dictionary<string, LanguageBuilder> byCode = new(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                Merge(byCode, path);
            }
        }

        return byCode.Values
            .Select(builder => builder.Build(platform))
            .OrderBy(catalogue => catalogue.Descriptor.NativeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Merge(Dictionary<string, LanguageBuilder> byCode, string path)
    {
        ParsedCatalogue? parsed = TryParse(path);

        if (parsed is null)
        {
            return;
        }

        if (!byCode.TryGetValue(parsed.Descriptor.Code, out LanguageBuilder? builder))
        {
            builder = new LanguageBuilder(parsed.Descriptor);
            byCode[parsed.Descriptor.Code] = builder;
        }

        builder.Apply(parsed.Descriptor, parsed.Strings);
    }

    private ParsedCatalogue? TryParse(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream, ParseOptions);

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                LocalizationLog.CatalogueNotAnObject(logger, path);
                return null;
            }

            // The file name is the fallback code, so a minimal file needs nothing but its strings.
            string code = Path.GetFileNameWithoutExtension(path);
            string nativeName = code;
            string englishName = code;

            if (root.TryGetProperty("language", out JsonElement language)
                && language.ValueKind == JsonValueKind.Object)
            {
                code = ReadString(language, "code") ?? code;
                nativeName = ReadString(language, "name") ?? code;
                englishName = ReadString(language, "englishName") ?? nativeName;
            }

            Dictionary<string, string> strings = new(StringComparer.OrdinalIgnoreCase);

            JsonElement container = root.TryGetProperty("strings", out JsonElement explicitStrings)
                && explicitStrings.ValueKind == JsonValueKind.Object
                ? explicitStrings
                : root;

            Flatten(container, prefix: string.Empty, strings);

            // Present only when the whole file is the string map, in which case they are not keys.
            strings.Remove("language");

            return new ParsedCatalogue(
                new LanguageDescriptor(code, nativeName, englishName),
                strings);
        }
        catch (JsonException exception)
        {
            LocalizationLog.CatalogueUnreadable(logger, path, exception);
            return null;
        }
        catch (IOException exception)
        {
            LocalizationLog.CatalogueUnreadable(logger, path, exception);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            LocalizationLog.CatalogueUnreadable(logger, path, exception);
            return null;
        }
    }

    /// <summary>
    /// Turns nested objects into dotted keys, so a catalogue can be grouped by screen while the
    /// views still address one flat key.
    /// </summary>
    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> target)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            string key = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Flatten(property.Value, key, target);
                    break;

                case JsonValueKind.String:
                    target[key] = property.Value.GetString() ?? string.Empty;
                    break;

                default:
                    // Numbers, arrays and booleans are not display text and are ignored on purpose.
                    break;
            }
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record ParsedCatalogue(
        LanguageDescriptor Descriptor,
        Dictionary<string, string> Strings);

    /// <summary>
    /// Accumulates the layers that make up one language.
    /// </summary>
    private sealed class LanguageBuilder
    {
        private readonly Dictionary<string, string> strings = new(StringComparer.OrdinalIgnoreCase);
        private LanguageDescriptor descriptor;

        public LanguageBuilder(LanguageDescriptor descriptor)
        {
            this.descriptor = descriptor;
        }

        public void Apply(LanguageDescriptor next, Dictionary<string, string> layer)
        {
            descriptor = next;

            foreach ((string key, string value) in layer)
            {
                strings[key] = value;
            }
        }

        /// <summary>
        /// Produces the catalogue one platform sees.
        /// </summary>
        /// <remarks>
        /// The variants are applied after every layer has been merged, so a file the user adds can
        /// override the neutral wording and the platform wording independently.
        /// </remarks>
        public LanguageCatalogue Build(string? platform)
        {
            Dictionary<string, string> resolved = new(StringComparer.OrdinalIgnoreCase);
            List<(string Key, string Value)> variants = [];

            foreach ((string key, string value) in strings)
            {
                if (!TrySplitPlatformKey(key, out string neutralKey, out string variant))
                {
                    resolved.TryAdd(key, value);
                }
                else if (platform is not null
                    && string.Equals(variant, platform, StringComparison.OrdinalIgnoreCase))
                {
                    variants.Add((neutralKey, value));
                }
            }

            foreach ((string key, string value) in variants)
            {
                resolved[key] = value;
            }

            return new LanguageCatalogue(descriptor, resolved);
        }
    }
}

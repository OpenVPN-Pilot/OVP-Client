using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace OpenVpnPilot.App.Localization;

/// <summary>
/// Resolves a localized string in XAML, written as {loc:Translate header.refresh}.
/// </summary>
/// <remarks>
/// The value is a dynamic resource reference rather than a literal, so replacing the published
/// catalogue updates every use of it in place. That is what allows the language to be switched
/// without reopening the window.
/// </remarks>
public sealed class TranslateExtension : MarkupExtension
{
    public TranslateExtension()
    {
        Key = string.Empty;
    }

    public TranslateExtension(string key)
    {
        Key = key;
    }

    /// <summary>
    /// The catalogue key, without the resource prefix.
    /// </summary>
    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            return string.Empty;
        }

        return new DynamicResourceExtension(LocalizationResourceBridge.KeyPrefix + Key)
            .ProvideValue(serviceProvider);
    }
}

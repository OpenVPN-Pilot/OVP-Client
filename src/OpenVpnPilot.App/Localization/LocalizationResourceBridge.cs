using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.Localization;

/// <summary>
/// Publishes the active language as application resources, so views can bind to a key.
/// </summary>
/// <remarks>
/// The catalogue is exposed as a merged resource dictionary rather than resolved key by key. A
/// language change replaces that dictionary wholesale, which is the one operation Avalonia is
/// guaranteed to propagate to every dynamic resource reference in the tree. Every visible label
/// therefore follows a language switch without the window being rebuilt.
/// </remarks>
public sealed class LocalizationResourceBridge : IDisposable
{
    /// <summary>
    /// Prefix that separates translated text from the palette and shape resources.
    /// </summary>
    public const string KeyPrefix = "Loc.";

    private readonly ILocalizer localizer;
    private Application? application;
    private ResourceDictionary? published;
    private bool disposed;

    public LocalizationResourceBridge(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        this.localizer = localizer;
    }

    /// <summary>
    /// Publishes the current catalogue and keeps it in step with the active language.
    /// </summary>
    public void Attach(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        this.application = application;
        Publish();

        localizer.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A language can be switched from a background thread, for example by a watched settings
        // file, and resource dictionaries belong to the user interface thread.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Publish();
            return;
        }

        Dispatcher.UIThread.Post(Publish);
    }

    private void Publish()
    {
        if (application is null)
        {
            return;
        }

        ResourceDictionary next = [];

        foreach (string key in localizer.Keys)
        {
            next[KeyPrefix + key] = localizer[key];
        }

        IList<IResourceProvider> merged = application.Resources.MergedDictionaries;

        if (published is not null)
        {
            merged.Remove(published);
        }

        merged.Add(next);
        published = next;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        localizer.LanguageChanged -= OnLanguageChanged;

        if (application is not null && published is not null)
        {
            application.Resources.MergedDictionaries.Remove(published);
        }

        published = null;
        application = null;
    }
}

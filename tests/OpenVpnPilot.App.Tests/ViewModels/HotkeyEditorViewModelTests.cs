using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Core.Abstractions;
using OpenVpnPilot.Core.Localization;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// How a stored combination is written in the settings screen.
/// </summary>
/// <remarks>
/// Read through the language files that ship, because the wording of a modifier is exactly what
/// differs between the platforms and a stub localizer would hide it.
/// </remarks>
public sealed class HotkeyEditorViewModelTests
{
    private static readonly string LanguageDirectory = Path.Combine(AppContext.BaseDirectory, "lang");

    /// <summary>
    /// The neutral wording is the stored form, so nothing about the display changed on Windows.
    /// </summary>
    [Theory]
    [InlineData("Control+Alt+V")]
    [InlineData("Control+Alt+Shift+Windows+F5")]
    [InlineData("Windows+D1")]
    public void GestureDisplay_WithTheNeutralWording_IsTheStoredCombination(string stored)
    {
        HotkeyEditorViewModel editor = new(HotkeyActions.ToggleQuickSwitcher, stored, Localizer(platform: null));

        Assert.Equal(stored, editor.GestureDisplay);
    }

    [Theory]
    [InlineData("Control+Alt+V", "⌃⌥V")]
    [InlineData("Control+Alt+Shift+Windows+F5", "⌃⌥⇧⌘F5")]
    [InlineData("Windows+Shift+X", "⇧⌘X")]
    public void GestureDisplay_OnMacOS_UsesTheSymbolsOnTheKeys(string stored, string shown)
    {
        HotkeyEditorViewModel editor = new(HotkeyActions.ToggleQuickSwitcher, stored, Localizer("macos"));

        Assert.Equal(shown, editor.GestureDisplay);
    }

    [Fact]
    public void GestureDisplay_ThatCannotBeRead_IsShownAsStored()
    {
        HotkeyEditorViewModel editor = new(HotkeyActions.ToggleQuickSwitcher, "Control+", Localizer("macos"));

        Assert.Equal("Control+", editor.GestureDisplay);
    }

    private static LocalizationManager Localizer(string? platform) =>
        new(new JsonLanguageCatalogueSource([LanguageDirectory], platform: platform));
}

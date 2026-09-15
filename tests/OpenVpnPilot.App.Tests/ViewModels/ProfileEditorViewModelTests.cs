using OpenVpnPilot.App.ViewModels;
using OpenVpnPilot.Data.Entities;
using OpenVpnPilot.OpenVpn.Configuration;

namespace OpenVpnPilot.App.Tests.ViewModels;

/// <summary>
/// The editor changes what was edited, refuses what OpenVPN would refuse, and carries an edit across
/// when the view is switched.
/// </summary>
public sealed class ProfileEditorViewModelTests
{
    private static readonly string Configuration = string.Join(
        "\n",
        "client",
        "remote vpn.example.com 1194 udp",
        "<ca>",
        "-----BEGIN CERTIFICATE-----",
        "MIIDQjCCAiqgAwIB",
        "-----END CERTIFICATE-----",
        "</ca>",
        "<key>",
        "-----BEGIN PRIVATE KEY-----",
        "MIIEvQIBADANBgkq",
        "-----END PRIVATE KEY-----",
        "</key>",
        string.Empty);

    private readonly FakeProfileStore store = new();

    [Fact]
    public async Task SavingWithoutAnEdit_LeavesTheConfigurationAlone()
    {
        ProfileEditorViewModel editor = await OpenAsync();

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Empty(store.ConfigurationUpdates);
    }

    [Fact]
    public async Task ChangingThePortInTheForm_RewritesOnlyTheRemoteLine()
    {
        ProfileEditorViewModel editor = await OpenAsync();

        editor.Port = 443;
        editor.SelectedProtocol = editor.Protocols.Single(choice => choice.Value == OvpnProtocol.Tcp);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            Configuration.Replace("remote vpn.example.com 1194 udp", "remote vpn.example.com 443 tcp", StringComparison.Ordinal),
            Assert.Single(store.ConfigurationUpdates));
    }

    /// <summary>
    /// A key pasted into the wrong place is caught before it is stored, not when the tunnel fails.
    /// </summary>
    [Fact]
    public async Task WhatOpenVpnWouldRefuse_IsNotSaved()
    {
        ProfileEditorViewModel editor = await OpenAsync();
        bool closed = false;
        editor.Closed += (_, _) => closed = true;

        editor.KeyText = "-----BEGIN CERTIFICATE-----\nMIIDQjCCAiqgAwIB\n-----END CERTIFICATE-----";

        Assert.True(editor.HasErrors);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Empty(store.ConfigurationUpdates);
        Assert.False(closed);
        Assert.Equal("editor.fixIssues", editor.StatusMessage);
    }

    [Fact]
    public async Task SwitchingViews_CarriesTheEditBothWays()
    {
        ProfileEditorViewModel editor = await OpenAsync();

        editor.Host = "vpn2.example.com";
        editor.ToggleViewCommand.Execute(null);

        Assert.True(editor.IsPlainText);
        Assert.Contains("remote vpn2.example.com 1194 udp", editor.ConfigurationText, StringComparison.Ordinal);

        editor.ConfigurationText = editor.ConfigurationText.Replace(" 1194 ", " 1195 ", StringComparison.Ordinal);
        editor.ToggleViewCommand.Execute(null);

        Assert.False(editor.IsPlainText);
        Assert.Equal(1195m, editor.Port);
        Assert.Equal("vpn2.example.com", editor.Host);
    }

    [Fact]
    public async Task ThePlainTextIsSavedWithTheLineEndingsTheFileHad()
    {
        ProfileEditorViewModel editor = await OpenAsync(startsInPlainText: true);

        editor.ConfigurationText = editor.ConfigurationText
            .Replace("\n", "\r\n", StringComparison.Ordinal)
            .Replace("1194", "1195", StringComparison.Ordinal);

        await editor.SaveCommand.ExecuteAsync(null);

        string saved = Assert.Single(store.ConfigurationUpdates);
        Assert.DoesNotContain("\r", saved, StringComparison.Ordinal);
        Assert.Contains("remote vpn.example.com 1195 udp\n", saved, StringComparison.Ordinal);
    }

    private async Task<ProfileEditorViewModel> OpenAsync(bool startsInPlainText = false)
    {
        Profile profile = store.Add("example-site");
        profile.Configuration = Configuration;

        ProfileEditorViewModel editor = new(
            store,
            new StubLocalizer(),
            new ProfileItemViewModel(profile, new StubLocalizer()),
            startsInPlainText);

        await editor.LoadAsync();
        return editor;
    }
}

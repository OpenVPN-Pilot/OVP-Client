using OpenVpnPilot.Core.Localization;
using OpenVpnPilot.Core.Vpn;

namespace OpenVpnPilot.Core.Tests.Localization;

/// <summary>
/// Covers which half of a status reaches the user in their own language.
/// </summary>
/// <remarks>
/// The distinction is the whole point of the reason code: a sentence this client wrote is
/// translated, and text that came from OpenVPN is passed on as it stands, because nobody can
/// translate what the tunnel said. Getting it the wrong way round shows English in a German
/// notification, which is how the reason code came to exist.
/// </remarks>
public sealed class VpnStatusTextTests
{
    [Fact]
    public void Describe_ReasonThisClientWrote_IsTranslated()
    {
        LocalizationManager localizer = Build();
        localizer.TrySetLanguage("de");

        VpnConnectionStatus status = new()
        {
            State = VpnConnectionState.Failed,
            Message = "No credentials were supplied for 'office'.",
            Reason = new VpnStatusReason
            {
                Code = VpnStatusReasonCode.CredentialsMissing,
                Arguments = ["office"],
            },
        };

        Assert.Equal("Keine Anmeldedaten fuer office.", localizer.Describe(status));
    }

    [Fact]
    public void Describe_ReasonWithoutArguments_IsTranslated()
    {
        LocalizationManager localizer = Build();
        localizer.TrySetLanguage("de");

        VpnConnectionStatus status = new()
        {
            State = VpnConnectionState.Failed,
            Message = "The server pushed a compression setting this client cannot apply.",
            Reason = new VpnStatusReason { Code = VpnStatusReasonCode.PushedCompressionRefused },
        };

        Assert.Equal("Komprimierung abgelehnt.", localizer.Describe(status));
    }

    [Fact]
    public void Describe_TextFromOpenVpn_IsPassedOnUnchanged()
    {
        LocalizationManager localizer = Build();
        localizer.TrySetLanguage("de");

        // What a push reply failure reports, which no catalogue can carry.
        VpnConnectionStatus status = new()
        {
            State = VpnConnectionState.Reconnecting,
            Message = "process-push-msg-failed",
        };

        Assert.Equal("process-push-msg-failed", localizer.Describe(status));
    }

    [Fact]
    public void Describe_NothingWentWrong_IsEmpty()
    {
        LocalizationManager localizer = Build();

        Assert.Equal(string.Empty, localizer.Describe(VpnConnectionStatus.Disconnected));
    }

    private static LocalizationManager Build()
    {
        StubSource source = new();

        source.Add(new LanguageCatalogue(
            new LanguageDescriptor("en", "English", "English"),
            new Dictionary<string, string>
            {
                ["reason.credentialsMissing"] = "No credentials for {0}.",
                ["reason.pushedCompressionRefused"] = "Compression refused.",
            }));

        source.Add(new LanguageCatalogue(
            new LanguageDescriptor("de", "Deutsch", "German"),
            new Dictionary<string, string>
            {
                ["reason.credentialsMissing"] = "Keine Anmeldedaten fuer {0}.",
                ["reason.pushedCompressionRefused"] = "Komprimierung abgelehnt.",
            }));

        return new LocalizationManager(source);
    }

    private sealed class StubSource : ILanguageCatalogueSource
    {
        private readonly List<LanguageCatalogue> catalogues = [];

        public void Add(LanguageCatalogue catalogue) => catalogues.Add(catalogue);

        public IReadOnlyList<LanguageCatalogue> Load() => catalogues.ToList();
    }
}

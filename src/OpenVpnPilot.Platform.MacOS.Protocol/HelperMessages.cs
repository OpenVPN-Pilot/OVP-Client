using System.Text.Json.Serialization;

namespace OpenVpnPilot.Platform.MacOS.Protocol;

/// <summary>
/// The version both sides must agree on before anything else is said.
/// </summary>
/// <remarks>
/// The application is updated often and without administrator rights, the helper rarely and only
/// with them, so the two will be out of step at some point. A mismatch is reported in words rather
/// than discovered as a request the other side misreads.
/// </remarks>
public static class HelperProtocol
{
    public const int Version = 1;

    /// <summary>
    /// The largest message either side accepts. A configuration with every certificate inline is a
    /// few tens of kilobytes; a megabyte leaves room without letting a caller exhaust the helper.
    /// </summary>
    public const int MaximumMessageBytes = 1024 * 1024;
}

/// <summary>
/// The kinds of message, as they appear on the wire.
/// </summary>
public static class HelperMessageType
{
    /// <summary>
    /// The first request of every session: the versions, and whether the caller is authorised.
    /// </summary>
    public const string Hello = "hello";

    /// <summary>
    /// Starts a tunnel. Answered with <see cref="Launched"/> or <see cref="Refused"/>.
    /// </summary>
    public const string Launch = "launch";

    /// <summary>
    /// Ends a tunnel this caller started. Answered with <see cref="Terminated"/>.
    /// </summary>
    public const string Terminate = "terminate";

    /// <summary>
    /// Lists the tunnels that belong to the caller's account. Answered with <see cref="Tunnels"/>.
    /// </summary>
    public const string List = "list";

    public const string Launched = "launched";

    public const string Terminated = "terminated";

    public const string Tunnels = "tunnels";

    /// <summary>
    /// A request that was not carried out, with the reason.
    /// </summary>
    public const string Refused = "refused";
}

/// <summary>
/// Why a request was refused. The wording shown to a person is chosen on the application side.
/// </summary>
public static class HelperRefusal
{
    /// <summary>
    /// The message could not be read, or it came before the versions were agreed.
    /// </summary>
    public const string Protocol = "protocol";

    /// <summary>
    /// The two sides speak different versions.
    /// </summary>
    public const string Version = "version";

    /// <summary>
    /// The caller may not start a configuration of its own, only one an administrator installed.
    /// </summary>
    public const string NotAuthorised = "not-authorised";

    /// <summary>
    /// The configuration asks for something the helper does not do as root.
    /// </summary>
    public const string Configuration = "configuration";

    /// <summary>
    /// A limit on tunnels or requests was reached.
    /// </summary>
    public const string Limit = "limit";

    /// <summary>
    /// The tunnel is unknown, or belongs to somebody else.
    /// </summary>
    public const string NotFound = "not-found";

    /// <summary>
    /// OpenVPN could not be started.
    /// </summary>
    public const string LaunchFailed = "launch-failed";

    /// <summary>
    /// The helper itself failed while carrying out the request. The reason is in its log.
    /// </summary>
    /// <remarks>
    /// A request is always answered, including when answering it went wrong, because a caller that
    /// is told nothing waits for a tunnel that does not exist.
    /// </remarks>
    public const string Failed = "failed";
}

/// <summary>
/// One request from the application or the companion command.
/// </summary>
public sealed record HelperRequest
{
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// The protocol the caller speaks. Carried by <see cref="HelperMessageType.Hello"/>.
    /// </summary>
    public int ProtocolVersion { get; init; }

    /// <summary>
    /// Who is calling, for the helper's own record. Never used for a decision.
    /// </summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// What to start. Carried by <see cref="HelperMessageType.Launch"/>.
    /// </summary>
    public LaunchSpecification? Launch { get; init; }

    /// <summary>
    /// The process a <see cref="HelperMessageType.Terminate"/> is about.
    /// </summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// How long a <see cref="HelperMessageType.Terminate"/> lets the process exit by itself before
    /// the helper ends it. The helper caps it, so no caller can hold it waiting.
    /// </summary>
    public int GraceMilliseconds { get; init; }
}

/// <summary>
/// Everything the helper needs to start one tunnel, and nothing it could be talked into misusing.
/// </summary>
/// <remarks>
/// There is no field for arbitrary command line options. The helper builds the command line itself
/// from these values, so there is nothing to smuggle an option through, and the options it needs
/// never have to be recognised in text a caller supplied.
///
/// The configuration travels as text rather than as a path. The helper writes its own copy where
/// only root can read it, which rules out a file being swapped between the check and the use, and
/// means root never opens a path a caller chose.
/// </remarks>
public sealed record LaunchSpecification
{
    // Every member here can arrive absent, and absent is null or zero: the serialisation the helper
    // is compiled with does not run property initialisers, so a default written as one would be a
    // default the helper never sees. Nothing in this record may be assumed to be present.

    /// <summary>
    /// The configuration itself, for a caller authorised to start its own.
    /// </summary>
    public string? Configuration { get; init; }

    /// <summary>
    /// The name of a file under the configurations directory an administrator installed. The only
    /// way an account that is not authorised can start anything.
    /// </summary>
    public string? InstalledConfiguration { get; init; }

    /// <summary>
    /// The loopback port the management interface is to listen on.
    /// </summary>
    public int ManagementPort { get; init; }

    /// <summary>
    /// The management password. Handed to OpenVPN through a pipe and never written anywhere.
    /// </summary>
    public string? ManagementPassword { get; init; }

    /// <summary>
    /// What OpenVPN is asked to report, from 0 to 11. Absent means zero, not the client's default.
    /// </summary>
    public int Verbosity { get; init; }

    /// <summary>
    /// Pushed options to accept, ignore or reject, which is how route protection is applied.
    /// </summary>
    public IReadOnlyList<PullFilterSpecification>? PullFilters { get; init; }
}

/// <summary>
/// One pull filter: what to do with pushed options that begin with some text.
/// </summary>
public sealed record PullFilterSpecification(string Action, string Text);

/// <summary>
/// One answer from the helper.
/// </summary>
public sealed record HelperResponse
{
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// The protocol the helper speaks. Carried by the answer to <see cref="HelperMessageType.Hello"/>.
    /// </summary>
    public int ProtocolVersion { get; init; }

    /// <summary>
    /// The helper's own version, so a stale installation can be named.
    /// </summary>
    public string? HelperVersion { get; init; }

    /// <summary>
    /// The version of the OpenVPN the helper runs, or null when it is missing.
    /// </summary>
    public string? OpenVpnVersion { get; init; }

    /// <summary>
    /// Where that OpenVPN is.
    /// </summary>
    public string? OpenVpnPath { get; init; }

    /// <summary>
    /// True when the caller may start configurations of its own.
    /// </summary>
    public bool Authorised { get; init; }

    /// <summary>
    /// Why the caller is or is not authorised, as a fact rather than a sentence.
    /// </summary>
    public string? Authorisation { get; init; }

    /// <summary>
    /// The OpenVPN process a <see cref="HelperMessageType.Launched"/> or
    /// <see cref="HelperMessageType.Terminated"/> is about.
    /// </summary>
    public int ProcessId { get; init; }

    /// <summary>
    /// One of the <see cref="HelperRefusal"/> codes.
    /// </summary>
    public string? RefusalCode { get; init; }

    /// <summary>
    /// A factual explanation for a refusal, such as the directive that was refused and its line.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// The caller's tunnels, in answer to <see cref="HelperMessageType.List"/>.
    /// </summary>
    public IReadOnlyList<TunnelDescription>? Tunnels { get; init; }
}

/// <summary>
/// One running tunnel, as the helper sees it.
/// </summary>
/// <param name="ProcessId">The OpenVPN process.</param>
/// <param name="StartedAt">When it was started.</param>
/// <param name="InThisSession">True when the session asking is the one that started it.</param>
public sealed record TunnelDescription(int ProcessId, DateTimeOffset StartedAt, bool InThisSession);

/// <summary>
/// Serialisation that works without reflection, which the ahead of time compiled helper requires.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperResponse))]
internal sealed partial class HelperJsonContext : JsonSerializerContext
{
}

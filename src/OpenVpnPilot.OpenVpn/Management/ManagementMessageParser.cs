using System.Globalization;
using System.Text;

namespace OpenVpnPilot.OpenVpn.Management;

/// <summary>
/// Turns raw management interface lines into typed messages.
/// </summary>
/// <remarks>
/// Notifications are prefixed with '>' and arrive asynchronously. Everything else is a reply to a
/// command. The parser never throws on malformed input: an unrecognised notification becomes an
/// <see cref="UnknownNotificationMessage"/> so that nothing is dropped without a trace.
/// </remarks>
public static class ManagementMessageParser
{
    private const string StatePrefix = ">STATE:";
    private const string ByteCountPrefix = ">BYTECOUNT:";
    private const string LogPrefix = ">LOG:";
    private const string HoldPrefix = ">HOLD:";
    private const string PasswordPrefix = ">PASSWORD:";
    private const string FatalPrefix = ">FATAL:";
    private const string InfoPrefix = ">INFO:";
    private const string EchoPrefix = ">ECHO:";

    private const string PasswordNeedMarker = "Need ";
    private const string PasswordVerificationMarker = "Verification Failed: ";
    private const string StaticChallengeMarker = "SC:";
    private const string DynamicChallengeMarker = "CRV1:";

    /// <summary>
    /// Parses one line. The line must already have its trailing newline removed.
    /// </summary>
    public static ManagementMessage Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (!line.StartsWith('>'))
        {
            return ParseCommandResponse(line);
        }

        if (line.StartsWith(StatePrefix, StringComparison.Ordinal))
        {
            return ParseState(line, line[StatePrefix.Length..]);
        }

        if (line.StartsWith(ByteCountPrefix, StringComparison.Ordinal))
        {
            return ParseByteCount(line, line[ByteCountPrefix.Length..]);
        }

        if (line.StartsWith(LogPrefix, StringComparison.Ordinal))
        {
            return ParseLog(line, line[LogPrefix.Length..]);
        }

        if (line.StartsWith(HoldPrefix, StringComparison.Ordinal))
        {
            return ParseHold(line, line[HoldPrefix.Length..]);
        }

        if (line.StartsWith(PasswordPrefix, StringComparison.Ordinal))
        {
            return ParsePassword(line, line[PasswordPrefix.Length..]);
        }

        if (line.StartsWith(FatalPrefix, StringComparison.Ordinal))
        {
            return new FatalMessage { RawLine = line, Text = line[FatalPrefix.Length..] };
        }

        if (line.StartsWith(InfoPrefix, StringComparison.Ordinal))
        {
            return new InfoMessage { RawLine = line, Text = line[InfoPrefix.Length..] };
        }

        if (line.StartsWith(EchoPrefix, StringComparison.Ordinal))
        {
            return ParseEcho(line, line[EchoPrefix.Length..]);
        }

        int separator = line.IndexOf(':', StringComparison.Ordinal);
        return new UnknownNotificationMessage
        {
            RawLine = line,
            Kind = separator > 1 ? line[1..separator] : line[1..],
            Payload = separator >= 0 ? line[(separator + 1)..] : string.Empty,
        };
    }

    private static CommandResponseMessage ParseCommandResponse(string line)
    {
        if (line.StartsWith("SUCCESS: ", StringComparison.Ordinal))
        {
            return new CommandResponseMessage
            {
                RawLine = line,
                Kind = CommandResponseKind.Success,
                Text = line["SUCCESS: ".Length..],
            };
        }

        if (line.StartsWith("ERROR: ", StringComparison.Ordinal))
        {
            return new CommandResponseMessage
            {
                RawLine = line,
                Kind = CommandResponseKind.Error,
                Text = line["ERROR: ".Length..],
            };
        }

        if (line == "END")
        {
            return new CommandResponseMessage
            {
                RawLine = line,
                Kind = CommandResponseKind.End,
                Text = string.Empty,
            };
        }

        return new CommandResponseMessage
        {
            RawLine = line,
            Kind = CommandResponseKind.Continuation,
            Text = line,
        };
    }

    // Field order observed on the wire:
    // time, name, description, local address, remote address, remote port, local port, local IPv6.
    private static StateMessage ParseState(string rawLine, string payload)
    {
        string[] fields = payload.Split(',');

        return new StateMessage
        {
            RawLine = rawLine,
            Timestamp = ParseUnixSeconds(Field(fields, 0)),
            Name = Field(fields, 1) ?? string.Empty,
            Description = Field(fields, 2),
            LocalAddress = Field(fields, 3),
            RemoteAddress = Field(fields, 4),
            RemotePort = ParseNullableInt(Field(fields, 5)),
            LocalPort = Field(fields, 6),
            LocalIpv6Address = Field(fields, 7),
        };
    }

    private static ManagementMessage ParseByteCount(string rawLine, string payload)
    {
        string[] fields = payload.Split(',');

        if (fields.Length < 2
            || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytesIn)
            || !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytesOut))
        {
            return new UnknownNotificationMessage
            {
                RawLine = rawLine,
                Kind = "BYTECOUNT",
                Payload = payload,
            };
        }

        return new ByteCountMessage
        {
            RawLine = rawLine,
            BytesIn = bytesIn,
            BytesOut = bytesOut,
        };
    }

    // The message text may itself contain commas, so only the first two fields are split off.
    private static LogMessage ParseLog(string rawLine, string payload)
    {
        string[] fields = payload.Split(',', 3);

        return new LogMessage
        {
            RawLine = rawLine,
            Timestamp = ParseUnixSeconds(Field(fields, 0)),
            Severity = ParseSeverity(Field(fields, 1)),
            Text = Field(fields, 2) ?? string.Empty,
        };
    }

    private static EchoMessage ParseEcho(string rawLine, string payload)
    {
        string[] fields = payload.Split(',', 2);

        return new EchoMessage
        {
            RawLine = rawLine,
            Timestamp = ParseUnixSeconds(Field(fields, 0)),
            Text = Field(fields, 1) ?? string.Empty,
        };
    }

    // Format is "Waiting for hold release:<seconds>", where the text itself contains a colon.
    private static HoldMessage ParseHold(string rawLine, string payload)
    {
        int separator = payload.LastIndexOf(':');
        string text = separator >= 0 ? payload[..separator] : payload;
        int timeout = separator >= 0 ? ParseNullableInt(payload[(separator + 1)..]) ?? 0 : 0;

        return new HoldMessage
        {
            RawLine = rawLine,
            Text = text,
            TimeoutSeconds = timeout,
        };
    }

    // Either "Need '<realm>' username/password", "Need '<realm>' password"
    // or "Verification Failed: '<realm>' ['<reason>']".
    private static ManagementMessage ParsePassword(string rawLine, string payload)
    {
        if (payload.StartsWith(PasswordNeedMarker, StringComparison.Ordinal))
        {
            string remainder = payload[PasswordNeedMarker.Length..];
            string? realm = ExtractQuoted(remainder);

            if (realm is not null)
            {
                return new PasswordRequestMessage
                {
                    RawLine = rawLine,
                    Realm = realm,
                    NeedsUsername = remainder.Contains("username", StringComparison.OrdinalIgnoreCase),
                    Challenge = ParseStaticChallenge(remainder),
                };
            }
        }

        if (payload.StartsWith(PasswordVerificationMarker, StringComparison.Ordinal))
        {
            string remainder = payload[PasswordVerificationMarker.Length..];
            string? realm = ExtractQuoted(remainder);

            if (realm is not null)
            {
                int realmEnd = remainder.IndexOf('\'', 1);
                string reason = realmEnd >= 0 ? remainder[(realmEnd + 1)..].Trim() : string.Empty;

                string detail = reason.Trim('[', ']', '\'', ' ');

                return new PasswordVerificationFailedMessage
                {
                    RawLine = rawLine,
                    Realm = realm,
                    Reason = detail,
                    Challenge = ParseDynamicChallenge(detail),
                };
            }
        }

        return new UnknownNotificationMessage
        {
            RawLine = rawLine,
            Kind = "PASSWORD",
            Payload = payload,
        };
    }

    /// <summary>
    /// Reads the static challenge a server appends as SC:echo,text.
    /// </summary>
    /// <remarks>
    /// The prompt runs to the end of the line and may itself contain commas, so only the echo flag
    /// is split off.
    /// </remarks>
    private static StaticChallenge? ParseStaticChallenge(string remainder)
    {
        int marker = remainder.IndexOf(StaticChallengeMarker, StringComparison.Ordinal);

        if (marker < 0)
        {
            return null;
        }

        string body = remainder[(marker + StaticChallengeMarker.Length)..];
        int comma = body.IndexOf(',', StringComparison.Ordinal);

        if (comma < 0)
        {
            // No flag field, so the whole remainder is the prompt and the safe reading is secret.
            return new StaticChallenge(body.Trim(), Echo: false);
        }

        return new StaticChallenge(
            body[(comma + 1)..].Trim(),
            Echo: body[..comma].Trim() == "1");
    }

    /// <summary>
    /// Reads a dynamic challenge, written as CRV1:flags:state:user:text.
    /// </summary>
    /// <remarks>
    /// The user name is base64 encoded because it may contain the field separator. The prompt is
    /// not, so the split stops after four fields and keeps the rest verbatim.
    /// </remarks>
    private static DynamicChallenge? ParseDynamicChallenge(string reason)
    {
        if (!reason.StartsWith(DynamicChallengeMarker, StringComparison.Ordinal))
        {
            return null;
        }

        string[] fields = reason[DynamicChallengeMarker.Length..].Split(':', 4);

        if (fields.Length < 4)
        {
            return null;
        }

        string flags = fields[0];

        return new DynamicChallenge(
            StateId: fields[1],
            Username: DecodeBase64(fields[2]),
            Text: fields[3],
            Echo: flags.Contains('E', StringComparison.Ordinal),
            ResponseRequired: flags.Contains('R', StringComparison.Ordinal));
    }

    private static string? DecodeBase64(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        byte[] buffer = new byte[value.Length];

        return Convert.TryFromBase64String(value, buffer, out int written)
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : null;
    }

    private static string? ExtractQuoted(string value)
    {
        if (value.Length == 0 || value[0] != '\'')
        {
            return null;
        }

        int end = value.IndexOf('\'', 1);
        return end > 0 ? value[1..end] : null;
    }

    private static LogSeverity ParseSeverity(string? flags)
    {
        if (string.IsNullOrEmpty(flags))
        {
            return LogSeverity.Verbose;
        }

        // OpenVPN may combine flags, so the most severe one present wins.
        if (flags.Contains('F', StringComparison.Ordinal))
        {
            return LogSeverity.Fatal;
        }

        if (flags.Contains('N', StringComparison.Ordinal))
        {
            return LogSeverity.NonFatalError;
        }

        if (flags.Contains('W', StringComparison.Ordinal))
        {
            return LogSeverity.Warning;
        }

        if (flags.Contains('I', StringComparison.Ordinal))
        {
            return LogSeverity.Informational;
        }

        if (flags.Contains('D', StringComparison.Ordinal))
        {
            return LogSeverity.Debug;
        }

        return LogSeverity.Verbose;
    }

    private static string? Field(string[] fields, int index)
    {
        if (index >= fields.Length)
        {
            return null;
        }

        string value = fields[index];
        return value.Length == 0 ? null : value;
    }

    private static DateTimeOffset ParseUnixSeconds(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;
    }

    private static int? ParseNullableInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }
}

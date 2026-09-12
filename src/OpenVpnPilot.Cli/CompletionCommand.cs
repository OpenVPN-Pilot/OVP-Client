using System.Globalization;

namespace OpenVpnPilot.Cli;

/// <summary>
/// Prints, or installs, a completion script for the shell the user names.
/// </summary>
/// <remarks>
/// Completion is the difference between a command that is typed and one that has to be looked up
/// first. The profile names come from the store at completion time rather than being baked into the
/// script, so a profile imported a minute ago completes without reloading the shell.
///
/// Installing writes the script to the application data directory and adds one line to the shell
/// profile that reads it. Pasting the whole script into a profile would leave a copy behind that
/// never gets the next version's fixes.
/// </remarks>
internal static class CompletionCommand
{
    /// <summary>
    /// Marks the line the installer owns, so installing twice replaces it rather than repeating it.
    /// </summary>
    private const string Marker = "# OpenVpnPilot completion";

    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string shell = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        bool install = args.Contains("--install", StringComparer.Ordinal);

        switch (shell)
        {
            case "powershell" or "pwsh":
                return install
                    ? Install("ovp-completion.ps1", PowerShellScript, PowerShellProfiles(), ". \"{0}\"")
                    : Print(PowerShellScript);

            case "bash":
                return install
                    ? Install("ovp-completion.bash", BashScript, BashProfiles(), "source \"{0}\"")
                    : Print(BashScript);

            case "zsh":
                return install
                    ? Install("ovp-completion.zsh", ZshScript, ZshProfiles(), "source \"{0}\"")
                    : Print(ZshScript);

            default:
                Console.Error.WriteLine("Name a shell: powershell, bash or zsh.");
                Console.Error.WriteLine("Add --install to write it into the shell profile.");
                return 1;
        }
    }

    private static int Print(string script)
    {
        Console.WriteLine(script);
        return 0;
    }

    /// <summary>
    /// Writes the script beside the application data and points the shell profiles at it.
    /// </summary>
    private static int Install(
        string fileName,
        string script,
        IReadOnlyList<string> profiles,
        string sourceLineFormat)
    {
        string scriptPath = Path.Combine(StoreFactory.Paths.DataDirectory, fileName);
        File.WriteAllText(scriptPath, script + Environment.NewLine);

        Console.WriteLine($"Wrote the completion script to {scriptPath}.");

        string line = string.Format(CultureInfo.InvariantCulture, sourceLineFormat, scriptPath);
        int touched = 0;

        foreach (string profile in profiles)
        {
            if (AddSourceLine(profile, line))
            {
                Console.WriteLine($"Referenced it from {profile}.");
                touched++;
            }
        }

        if (touched == 0)
        {
            Console.Error.WriteLine("No shell profile could be written. Add this line to yours by hand:");
            Console.Error.WriteLine($"  {line}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("Open a new shell, or run that line once, to use it now.");
        return 0;
    }

    /// <summary>
    /// Adds the line that reads the script, replacing an earlier one rather than repeating it.
    /// </summary>
    private static bool AddSourceLine(string profilePath, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);

            List<string> lines = File.Exists(profilePath)
                ? [.. File.ReadAllLines(profilePath)]
                : [];

            // The marker and the line after it belong to this installer and are rewritten together.
            int existing = lines.FindIndex(text => text.StartsWith(Marker, StringComparison.Ordinal));

            if (existing >= 0)
            {
                lines.RemoveRange(existing, Math.Min(2, lines.Count - existing));
            }

            lines.Add(Marker);
            lines.Add(line);

            File.WriteAllLines(profilePath, lines);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{profilePath} could not be written: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// The profiles of both PowerShell editions, because a machine commonly has both.
    /// </summary>
    /// <remarks>
    /// Only an edition that is already set up is touched. If neither is, the modern one is created,
    /// because that is what a new installation has.
    /// </remarks>
    private static List<string> PowerShellProfiles()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        string modern = Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1");
        string windows = Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1");

        List<string> existing = new[] { modern, windows }
            .Where(path => Directory.Exists(Path.GetDirectoryName(path)!))
            .ToList();

        return existing.Count > 0 ? existing : [modern];
    }

    private static List<string> BashProfiles() =>
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bashrc")];

    /// <summary>
    /// Where an interactive zsh reads its configuration from, which on macOS is the default shell.
    /// </summary>
    private static List<string> ZshProfiles() =>
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zshrc")];

    /// <summary>
    /// Offers the commands, and the stored profile names wherever a command takes one.
    /// </summary>
    /// <remarks>
    /// Which of the two is offered depends on the word being completed, not only on how many words
    /// have been typed: after "ovp con" with a space, the word is empty and the profile names are
    /// wanted, while "ovp con" without one is still the command being spelled.
    /// </remarks>
    private const string PowerShellScript = """
        Register-ArgumentCompleter -Native -CommandName ovp -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            $commands = @('doctor','start','stop','list','status','connect','disconnect','import','export','pack','unpack','favourite','remove','completion','help')
            $takesProfile = @('connect','con','conn','disconnect','dis','favourite','fav','remove','rm')
            $elements = @($commandAst.CommandElements)

            $completingCommand = ($elements.Count -le 1) -or (($elements.Count -eq 2) -and $wordToComplete)

            if ($completingCommand) {
                $commands | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                }
                return
            }

            if ($elements[1].ToString() -in $takesProfile) {
                ovp list --names 2>$null | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                    $name = $_
                    $insert = if ($name -match '\s') { "'" + $name + "'" } else { $name }
                    [System.Management.Automation.CompletionResult]::new($insert, $name, 'ParameterValue', $name)
                }
            }
        }
        """;

    private const string BashScript = """
        _ovp_complete() {
            local current command commands names
            current="${COMP_WORDS[COMP_CWORD]}"
            command="${COMP_WORDS[1]}"
            commands="doctor start stop list status connect disconnect import export pack unpack favourite remove completion help"

            if [ "$COMP_CWORD" -eq 1 ]; then
                COMPREPLY=( $(compgen -W "$commands" -- "$current") )
                return
            fi

            case "$command" in
                connect|con|conn|disconnect|dis|favourite|fav|remove|rm)
                    names="$(ovp list --names 2>/dev/null)"
                    local IFS=$'\n'
                    COMPREPLY=( $(compgen -W "$names" -- "$current") )
                    ;;
            esac
        }
        complete -F _ovp_complete ovp
        """;

    /// <summary>
    /// The same offer for zsh, written in its own completion system rather than through bash's.
    /// </summary>
    /// <remarks>
    /// zsh can run a bash completion through bashcompinit, and the result quotes names with spaces
    /// in them wrongly. This one is native: compadd takes the names as separate words, so a profile
    /// called "site alpha" completes as one argument.
    /// </remarks>
    private const string ZshScript = """
        _ovp_complete() {
            local -a commands names
            commands=(doctor start stop list status connect disconnect import export pack unpack favourite remove completion help)

            if (( CURRENT == 2 )); then
                compadd -a commands
                return
            fi

            case "${words[2]}" in
                connect|con|conn|disconnect|dis|favourite|fav|remove|rm)
                    names=("${(@f)$(ovp list --names 2>/dev/null)}")
                    compadd -a names
                    ;;
            esac
        }

        # compdef needs the completion system, which a shell that has not called compinit does not
        # have. Loaded here rather than assumed, because a profile that never set it up would
        # otherwise fail with compdef not found on every new shell.
        if ! whence compdef > /dev/null; then
            autoload -Uz compinit && compinit -u
        fi

        compdef _ovp_complete ovp
        """;
}

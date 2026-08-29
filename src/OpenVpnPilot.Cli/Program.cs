using Microsoft.Data.Sqlite;
using OpenVpnPilot.Cli;
using OpenVpnPilot.Core.Ipc;

// Nothing a command can run into is worth showing as a stack trace. A terminal command reports what
// went wrong in a sentence and returns a code; the trace helps whoever wrote it and nobody else.
try
{
    return await CommandRunner.RunAsync(args);
}
catch (SqliteException exception)
{
    Console.Error.WriteLine($"The profile store could not be read: {exception.Message}");

    if (PilotCommandClient.IsApplicationRunning())
    {
        // The application holds the same store. Reading it while a bulk change is being written can
        // fail, and the command that failed is almost always one the application could do instead.
        Console.Error.WriteLine(
            "OpenVpnPilot is running and is writing to the same store. Try again in a moment, or "
            + "close it with: ovp stop");
    }
    else
    {
        Console.Error.WriteLine(
            "The store lives beside the settings under the application data directory. A copy of it "
            + "can be opened with any SQLite tool if it has to be recovered.");
    }

    return 6;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"A file could not be read or written: {exception.Message}");
    return 6;
}

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OpenVpnPilot.Platform.Windows.Power;

namespace OpenVpnPilot.App.Tests.Services;

/// <summary>
/// The power manager accepts the registration, and taking it back leaves nothing behind.
/// </summary>
/// <remarks>
/// A sleep cannot be provoked from a test without taking the machine running it to sleep, so what is
/// covered is the part that fails silently otherwise: a registration the power manager refused
/// writes a warning and raises nothing, ever.
/// </remarks>
public sealed class WindowsPowerEventsTests
{
    [PowerManagerFact]
    [SupportedOSPlatform("windows")]
    public void Start_RegistersWithThePowerManagerWithoutComplaint()
    {
        CountingLogger logger = new();

        using (WindowsPowerEvents events = new(logger))
        {
            events.Start();
            events.Start();
        }

        Assert.Equal(0, logger.Warnings);
    }

    [PowerManagerFact]
    [SupportedOSPlatform("windows")]
    public void Dispose_Twice_IsHarmless()
    {
        WindowsPowerEvents events = new(new CountingLogger());
        events.Start();

        events.Dispose();
        events.Dispose();
    }

    private sealed class CountingLogger : ILogger<WindowsPowerEvents>
    {
        private int warnings;

        public int Warnings => Volatile.Read(ref warnings);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Interlocked.Increment(ref warnings);
            }
        }
    }
}

/// <summary>
/// A test of the Windows power manager, which only means something on Windows.
/// </summary>
public sealed class PowerManagerFactAttribute : FactAttribute
{
    public PowerManagerFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Depends on the Windows power manager.";
        }
    }
}

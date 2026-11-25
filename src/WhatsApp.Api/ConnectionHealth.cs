using WhatsApp.Protocol.Binary;

namespace WhatsApp.Api;

public enum KeepaliveProbeOutcome
{
    Ok = 0,
    Skipped = 1,
    TransientFailure = 2,
    FatalFailure = 3
}

public enum HealthAction
{
    Skip = 0,
    Probe = 1,
    Reconnect = 2,
    Stop = 3
}

/// <summary>
/// Monotonic connection health state machine matching <c>wacore/src/protocol/keepalive.rs</c>
/// and <c>wacore/src/protocol/health.rs</c>.
/// </summary>
public sealed class ConnectionHealth
{
    public static readonly TimeSpan KeepAliveIntervalMin = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan KeepAliveIntervalMax = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KeepAliveResponseDeadline = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DeadSocketTime = TimeSpan.FromSeconds(20);
    public const int MaxConsecutiveFailures = 3;

    private long? _failedProbeDeadlineTicks;

    public int Failures { get; private set; }

    public static bool FailuresAreTerminal(int failures) => failures >= MaxConsecutiveFailures;

    public static bool IsDeadSocket(
        TimeProvider timeProvider,
        long? armedTicks,
        long? lastReceivedTicks,
        long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (armedTicks is not { } armed)
        {
            return false;
        }

        if (lastReceivedTicks is { } received && received >= armed)
        {
            return false;
        }

        return timeProvider.GetElapsedTime(armed, nowTicks) > DeadSocketTime;
    }

    public HealthAction BeforeProbe(
        TimeProvider timeProvider,
        long nowTicks,
        long? lastReceivedTicks,
        long? firstSendSinceReceiveTicks)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (lastReceivedTicks is { } received
            && timeProvider.GetElapsedTime(received, nowTicks) < KeepAliveIntervalMin)
        {
            Restore();
            return HealthAction.Skip;
        }

        if (Failures > 0
            && firstSendSinceReceiveTicks == _failedProbeDeadlineTicks
            && IsDeadSocket(timeProvider, firstSendSinceReceiveTicks, lastReceivedTicks, nowTicks))
        {
            return HealthAction.Reconnect;
        }

        return HealthAction.Probe;
    }

    public HealthAction ProbeCompleted(
        KeepaliveProbeOutcome outcome,
        TimeProvider timeProvider,
        long nowTicks,
        long? receivedBeforeTicks,
        long? receivedAfterTicks,
        long? firstSendSinceReceiveTicks)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        switch (outcome)
        {
            case KeepaliveProbeOutcome.Skipped:
                return HealthAction.Skip;

            case KeepaliveProbeOutcome.FatalFailure:
                return HealthAction.Stop;

            case KeepaliveProbeOutcome.Ok:
                Restore();
                break;

            case KeepaliveProbeOutcome.TransientFailure:
                _failedProbeDeadlineTicks = receivedBeforeTicks == receivedAfterTicks
                    ? firstSendSinceReceiveTicks
                    : null;
                Failures++;
                if (FailuresAreTerminal(Failures))
                {
                    return HealthAction.Reconnect;
                }
                break;
        }

        return IsDeadSocket(timeProvider, firstSendSinceReceiveTicks, receivedAfterTicks, nowTicks)
            ? HealthAction.Reconnect
            : HealthAction.Skip;
    }

    private void Restore()
    {
        Failures = 0;
        _failedProbeDeadlineTicks = null;
    }
}

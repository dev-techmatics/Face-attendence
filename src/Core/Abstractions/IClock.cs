namespace FaceAttendance.Core.Abstractions;

/// <summary>Injectable clock so time-window rules (duplicate punches) are testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

namespace AutoPower.Data;

/// <summary>
/// Thrown when a schedule write would overwrite a row whose power transition already started or finished.
/// </summary>
public sealed class ScheduleStateConflictException : InvalidOperationException
{
    public ScheduleStateConflictException()
    {
    }

    public ScheduleStateConflictException(string message)
        : base(message)
    {
    }

    public ScheduleStateConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

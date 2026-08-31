namespace Pinboard.App.Services;

public sealed class RevisionConflictException : InvalidOperationException
{
    public RevisionConflictException(long expectedRevision, long actualRevision)
        : base($"The Pinboard scene changed while it was being saved. Expected revision {expectedRevision}, but the current revision is {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public long ExpectedRevision { get; }
    public long ActualRevision { get; }
}

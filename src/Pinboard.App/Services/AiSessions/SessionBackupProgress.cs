namespace Pinboard.App.Services.AiSessions;

public enum SessionBackupStage { Snapshot, LocalStore, Upload, Confirm }

public sealed record SessionBackupActivity(string Key, string Title, SessionBackupStage Stage, long CompletedBytes = 0, long TotalBytes = 0)
{
    public double ByteFraction => TotalBytes > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1) : 0;
    public double Fraction(bool cloud) => Stage switch
    {
        SessionBackupStage.Snapshot => 0,
        SessionBackupStage.LocalStore => ByteFraction * (cloud ? .1 : .95),
        SessionBackupStage.Upload => .1 + ByteFraction * .85,
        SessionBackupStage.Confirm => .99,
        _ => 0
    };
}

public sealed record SessionBackupProgress(int Total, int Completed, int LocalSaved, int CloudSaved, int Queued,
    int Retrying, int Warnings, int Active, bool CloudTarget, bool Paused, SessionBackupActivity? Current, int Blocked = 0)
{
    public int AutoRetrying => Math.Max(0, Retrying - Blocked);
    public int Waiting => Math.Max(0, Total - Completed - Retrying - Warnings - Active);
    public int Problems => Retrying + Warnings;
    public bool IsComplete => Total > 0 && Completed == Total && Active == 0 && Queued == 0;
    public double Percentage => Total == 0 ? 0 : IsComplete ? 100
        : Math.Min(99.9, 100d * (Completed + (Active > 0 ? Current?.Fraction(CloudTarget) ?? 0 : 0)) / Total);
}

public interface ISessionTransferProgressSource
{
    // Bytes acknowledged by the remote server, not merely buffered by the network client.
    event Action<long, long>? TransferProgress;
}

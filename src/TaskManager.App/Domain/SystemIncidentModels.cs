namespace TaskManager.Domain;

/// <summary>Task Manager本体の異常終了と復旧状態を表す。</summary>
public sealed class SystemIncidentRecord
{
    // 異常終了、復旧、通知、確認に必要な情報を保持する。
    public string Identifier { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public int ExitCode { get; set; }
    public int RestartCount { get; set; }
    public string Message { get; set; } = string.Empty;
    public string CrashLogPath { get; set; } = string.Empty;
    public string RecoveryLogPath { get; set; } = string.Empty;
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}

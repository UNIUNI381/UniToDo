using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>作業ログの重複に明示確認が必要なことを表す。</summary>
public sealed class TimeEntryOverlapException(
    IReadOnlyList<TimeEntryRecord> conflicts)
    : InvalidOperationException("既存の作業ログと時間が重複しています。確認後にallowOverlapを指定してください。")
{
    // 重複している既存ログを保持する。
    public IReadOnlyList<TimeEntryRecord> Conflicts { get; } = conflicts;
}

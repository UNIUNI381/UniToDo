namespace TaskManager.Domain;

/// <summary>1回の作業時間ログを表す。</summary>
public sealed class TimeEntryRecord
{
    // 作業区間、関連先、警告・確認状態を保持する。
    public string Identifier { get; set; } = string.Empty;
    public string? TaskIdentifier { get; set; }
    public string? ProjectIdentifier { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ProjectNameSnapshot { get; set; } = string.Empty;
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public string StopReason { get; set; } = string.Empty;
    public string Source { get; set; } = TaskConstants.ScreenSource;
    public DateTimeOffset? WarningAt { get; set; }
    public DateTimeOffset? NextWarningAt { get; set; }
    public bool NeedsReview { get; set; }
    public string ReviewReason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
}

/// <summary>自由活動タイマーの開始内容を表す。</summary>
public sealed class TimeEntryStartRequest
{
    // タスクを持たない活動名と任意のプロジェクトを保持する。
    public string Title { get; set; } = string.Empty;
    public string? ProjectIdentifier { get; set; }
}

/// <summary>完了済み作業ログの追加・更新内容を表す。</summary>
public sealed class TimeEntryMutationRequest
{
    // 表示内容、関連先、時間範囲、重複確認を保持する。
    public string? TaskIdentifier { get; set; }
    public string? ProjectIdentifier { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public bool AllowOverlap { get; set; }
}

/// <summary>実行中作業ログの開始時刻修正内容を表す。</summary>
public sealed class ActiveTimeEntryStartUpdateRequest
{
    // 修正後の開始日時と重複確認結果を保持する。
    public DateTimeOffset StartAt { get; set; }
    public bool AllowOverlap { get; set; }
}

/// <summary>作業ログの重複確認に返す競合情報を表す。</summary>
public sealed class TimeEntryOverlapResult
{
    // 保存可否と重複している既存ログを保持する。
    public bool RequiresConfirmation { get; init; } = true;
    public IReadOnlyList<TimeEntryRecord> Conflicts { get; init; } = [];
}

/// <summary>期間別作業時間レポートを表す。</summary>
public sealed class TimeReportResult
{
    // 集計範囲、総時間、時系列、プロジェクト・タスク内訳を保持する。
    public string Period { get; init; } = "day";
    public DateTimeOffset RangeStart { get; init; }
    public DateTimeOffset RangeEnd { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public long TotalSeconds { get; init; }
    public int ReviewCount { get; init; }
    public int OverlapCount { get; init; }
    public IReadOnlyList<TimeReportBucket> Buckets { get; init; } = [];
    public IReadOnlyList<TimeReportProjectSummary> Projects { get; init; } = [];
    public IReadOnlyList<TimeReportTaskSummary> Tasks { get; init; } = [];
}

/// <summary>レポート上の1時間または1日の集計枠を表す。</summary>
public sealed class TimeReportBucket
{
    // 集計枠の境界とプロジェクト別秒数を保持する。
    public DateTimeOffset StartAt { get; init; }
    public DateTimeOffset EndAt { get; init; }
    public long TotalSeconds { get; init; }
    public IReadOnlyList<TimeReportProjectDuration> Projects { get; init; } = [];
}

/// <summary>集計枠内のプロジェクト別作業秒数を表す。</summary>
public sealed class TimeReportProjectDuration
{
    // プロジェクト識別子と秒数を保持する。
    public string ProjectIdentifier { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public long TotalSeconds { get; init; }
}

/// <summary>期間全体のプロジェクト別集計を表す。</summary>
public sealed class TimeReportProjectSummary
{
    // プロジェクト識別子、名称、合計、件数を保持する。
    public string ProjectIdentifier { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public long TotalSeconds { get; init; }
    public int EntryCount { get; init; }
}

/// <summary>期間全体のタスク別集計を表す。</summary>
public sealed class TimeReportTaskSummary
{
    // タスク識別子、名称、所属プロジェクト、合計、件数を保持する。
    public string TaskIdentifier { get; init; } = string.Empty;
    public string TaskTitle { get; init; } = string.Empty;
    public string ProjectIdentifier { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public long TotalSeconds { get; init; }
    public int EntryCount { get; init; }
}

/// <summary>長時間タイマー定期処理の結果を表す。</summary>
public sealed class TimeTrackingAutomationResult
{
    // 新しく警告したログと自動停止したログを保持する。
    public IReadOnlyList<TimeEntryRecord> WarnedEntries { get; init; } = [];
    public IReadOnlyList<TimeEntryRecord> AutoStoppedEntries { get; init; } = [];
}

/// <summary>作業ログで共有する固定値を定義する。</summary>
public static class TimeTrackingConstants
{
    // 終了理由、未割当識別子、警告猶予を保持する。
    public const string CompletedStopReason = "completed";
    public const string InterruptedStopReason = "interrupted";
    public const string PostponedStopReason = "postponed";
    public const string CancelledStopReason = "cancelled";
    public const string ReplacedStopReason = "replaced";
    public const string ManualStopReason = "manual";
    public const string AutomaticTimeoutStopReason = "auto-timeout";
    public const string VoidedStopReason = "voided";
    public const string UnassignedProjectIdentifier = "__unassigned__";
    public const string FreeActivityTaskIdentifier = "__free__";
    public const int WarningGraceMinutes = 60;
}

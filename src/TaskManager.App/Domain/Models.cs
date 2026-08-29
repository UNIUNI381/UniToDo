using System.Text.Json.Serialization;

namespace TaskManager.Domain;

/// <summary>永続化するタスク本体と計算結果を表す。</summary>
public sealed class ManagedTask
{
    // タスクを一意に識別する値を保持する。
    public string Identifier { get; set; } = string.Empty;
    public string? ProjectIdentifier { get; set; }
    public string Category { get; set; } = "仕事";
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string? ParentIdentifier { get; set; }
    public string Status { get; set; } = TaskConstants.ReadyStatus;
    public DateTimeOffset? DeadlineAt { get; set; }
    public string DeadlineType { get; set; } = TaskConstants.NoDeadlineType;
    public string DeadlineOrigin { get; set; } = ProjectConstants.AutomaticDeadlineOrigin;
    public int EstimatedMinutes { get; set; } = 30;
    public int RemainingMinutes { get; set; } = 30;
    public int Importance { get; set; } = 3;
    public DateTimeOffset? EarliestStartAt { get; set; }
    public List<string> DependencyIdentifiers { get; set; } = [];
    public string RequiredContext { get; set; } = "PC";
    public string CompletionCondition { get; set; } = string.Empty;
    public bool Splittable { get; set; } = true;
    public double AiConfidence { get; set; }
    public string AiReferenceKey { get; set; } = string.Empty;
    public string Source { get; set; } = TaskConstants.ScreenSource;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? FollowUpAt { get; set; }
    public DateTimeOffset? FollowUpNotifiedAt { get; set; }
    public int DeferralCount { get; set; }
    public string ValidationResult { get; set; } = string.Empty;
    public int SuggestedMinutes { get; set; }
    public double PriorityScore { get; set; }
    public int? SlackMinutes { get; set; }
    public string RecommendationReason { get; set; } = string.Empty;
    public string? DraftBatchIdentifier { get; set; }
}

/// <summary>優先順位と自動処理に利用するユーザー設定を表す。</summary>
public sealed class TaskManagerSettings
{
    // 日々の活動可能時間と計算係数を保持する。
    public string CalendarIdentifiers { get; set; } = "primary";
    public string CalendarEmbedUrl { get; set; } = string.Empty;
    public string ActivityStart { get; set; } = "08:00";
    public string ActivityEnd { get; set; } = "22:00";
    public double BufferRatio { get; set; } = 0.2;
    public int MaximumWorkMinutes { get; set; } = 50;
    public int MinimumWorkMinutes { get; set; } = 15;
    public int FollowUpGraceMinutes { get; set; } = 10;
    public int MorningHour { get; set; } = 8;
    public int CalendarLookaheadDays { get; set; } = 14;
    public int CalendarBufferMinutes { get; set; } = 10;
    public double DeadlineWeight { get; set; } = 0.45;
    public double ImportanceWeight { get; set; } = 0.25;
    public double UnblockWeight { get; set; } = 0.10;
    public double DeferralWeight { get; set; } = 0.10;
    public double ContinuityWeight { get; set; } = 0.10;
    public double AgingWeight { get; set; } = 0.10;
    public string CurrentContext { get; set; } = "PC";
    public int DefaultPostponeMinutes { get; set; } = 60;
    public int FutureDailyCapacityMinutes { get; set; } = 240;
    public bool TreatAllDayAsBusy { get; set; }
    public string TimeZoneIdentifier { get; set; } = "Tokyo Standard Time";
    public bool NotificationsEnabled { get; set; } = true;
    public int LongTimerWarningMinutes { get; set; } = 180;

    // 利用者が設定したCodex音声入力の送信先を保持する。
    public string CodexThreadIdentifier { get; set; } = TaskConstants.DefaultCodexThreadIdentifier;
}

/// <summary>カレンダーから読み取った予定を表す。</summary>
public sealed class CalendarEventRecord
{
    // 外部予定の識別情報と時間範囲を保持する。
    public string EventIdentifier { get; set; } = string.Empty;
    public string CalendarIdentifier { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public string Location { get; set; } = string.Empty;
    public bool IsAllDay { get; set; }
    public bool IsBusy { get; set; } = true;
    public DateTimeOffset? ExternalUpdatedAt { get; set; }
}

/// <summary>カレンダーウィジェットへ表示する予定を表す。</summary>
public sealed class CalendarWidgetEvent
{
    // 予定の識別情報と画面表示に必要な最小項目だけを保持する。
    public string EventIdentifier { get; init; } = string.Empty;
    public string CalendarIdentifier { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public DateTimeOffset StartAt { get; init; }
    public DateTimeOffset EndAt { get; init; }
    public string Location { get; init; } = string.Empty;
    public bool IsAllDay { get; init; }
    public bool IsBusy { get; init; } = true;
}

/// <summary>カレンダーウィジェットへ表示するタスク期限を表す。</summary>
public sealed class CalendarWidgetDeadline
{
    // タスク、プロジェクト、期限表示に必要な最小項目だけを保持する。
    public string TaskIdentifier { get; init; } = string.Empty;
    public string? ProjectIdentifier { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateTimeOffset DeadlineAt { get; init; }
    public string DeadlineType { get; init; } = TaskConstants.TargetDeadlineType;
    public string Status { get; init; } = string.Empty;
}

/// <summary>ダッシュボードのカレンダーウィジェット表示内容を表す。</summary>
public sealed class CalendarWidgetResult
{
    // 7日分の表示範囲、埋め込み設定、予定、作業ログを保持する。
    public DateTimeOffset RangeStart { get; init; }
    public DateTimeOffset RangeEnd { get; init; }
    public string TimeZoneIdentifier { get; init; } = string.Empty;
    public string ActivityStart { get; init; } = "08:00";
    public string ActivityEnd { get; init; } = "22:00";
    public string EmbedUrl { get; init; } = string.Empty;
    public IReadOnlyList<CalendarWidgetEvent> Events { get; init; } = [];
    public IReadOnlyList<CalendarWidgetDeadline> Deadlines { get; init; } = [];
    public IReadOnlyList<TimeEntryRecord> TimeEntries { get; set; } = [];
}

/// <summary>現在の空き時間を表す。</summary>
public sealed record AvailableSlot(
    int AvailableMinutes,
    string Reason,
    string NextEventTitle,
    string NextEventLocation,
    DateTimeOffset SlotEnd);

/// <summary>1件のタスクに対する優先度評価を表す。</summary>
public sealed class TaskEvaluation
{
    // 評価対象と各加重要素を保持する。
    public required ManagedTask Task { get; init; }
    public DateTimeOffset? EffectiveDeadlineAt { get; init; }
    public double DeadlineRisk { get; init; }
    public double ImportanceScore { get; init; }
    public double UnblockScore { get; init; }
    public double DeferralScore { get; init; }
    public double ContinuityScore { get; init; }
    public double AgingScore { get; init; }
    public double? SlackMinutes { get; init; }
    public int SuggestedMinutes { get; init; }
    public bool ProtectsEarlierDeadlines { get; init; } = true;
    public bool StrictDeadlineInfeasible { get; init; }
    public DateTimeOffset? StrictPressureDeadlineAt { get; init; }
    public double PriorityScore { get; init; }
    public string Reason { get; init; } = string.Empty;
}

/// <summary>ダッシュボードへ返す推薦結果を表す。</summary>
public sealed class RecommendationResult
{
    // 推薦、候補評価、カレンダー、現在タイマー、要確認ログを保持する。
    public TaskEvaluation? Recommendation { get; init; }
    [JsonIgnore]
    public IReadOnlyList<TaskEvaluation> Evaluations { get; init; } = [];
    public bool CanForceRecommendation { get; init; }
    public required AvailableSlot CurrentSlot { get; init; }
    public string EmptyReason { get; init; } = string.Empty;
    public ManagedTask? FollowUpTask { get; init; }
    public DateTimeOffset? CalendarUpdatedAt { get; init; }
    public string CalendarError { get; init; } = string.Empty;
    public CalendarWidgetResult CalendarWidget { get; init; } = new();
    public TimeEntryRecord? ActiveTimeEntry { get; set; }
    public IReadOnlyList<TimeEntryRecord> ReviewTimeEntries { get; set; } = [];
}

/// <summary>操作履歴の1件を表す。</summary>
public sealed class HistoryRecord
{
    // 履歴の識別情報と表示内容を保持する。
    public long Identifier { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string TaskIdentifier { get; set; } = string.Empty;
    public string ProjectIdentifier { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

/// <summary>AIがまとめて登録する下書き群を表す。</summary>
public sealed class DraftBatchRequest
{
    // 親タスク、冪等キー、下書き一覧を保持する。
    public string? ParentIdentifier { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public List<ManagedTask> Tasks { get; set; } = [];
}

/// <summary>下書きタスクの仮参照キーと実IDの対応を表す。</summary>
public sealed class DraftTaskMapping
{
    // AIが指定した仮参照キーと保存済みタスクIDを保持する。
    public string AiReferenceKey { get; set; } = string.Empty;
    public string TaskIdentifier { get; set; } = string.Empty;
}

/// <summary>下書きバッチの保存結果と後処理状態を表す。</summary>
public sealed class DraftBatchCreationResult
{
    // 正式ID、互換ID、保存状態、対応表、警告を保持する。
    public string BatchIdentifier { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public bool Saved { get; set; }
    public int TaskCount { get; set; }
    public bool PostProcessingSucceeded { get; set; } = true;
    public string Warning { get; set; } = string.Empty;
    public bool Replayed { get; set; }
    public List<DraftTaskMapping> TaskMappings { get; set; } = [];
}

/// <summary>保存済み下書きバッチの読取情報を表す。</summary>
public sealed class DraftBatchRecord
{
    // バッチ本体、処理状態、関連プロジェクト、下書き一覧を保持する。
    public string BatchIdentifier { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ParentIdentifier { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public bool PostProcessingSucceeded { get; set; } = true;
    public string Warning { get; set; } = string.Empty;
    public int TaskCount { get; set; }
    public List<string> ProjectIdentifiers { get; set; } = [];
    public List<DraftTaskMapping> TaskMappings { get; set; } = [];
    public List<ManagedTask> Tasks { get; set; } = [];
}

/// <summary>プロジェクト本体と関連設定を表す。</summary>
public sealed class ProjectRecord
{
    // プロジェクトの識別情報と関連データを保持する。
    public string Identifier { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Status { get; set; } = ProjectConstants.ActiveStatus;
    public int? ColorHue { get; set; }
    public int? ColorSaturation { get; set; }
    public int? ColorValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ProjectAlias> Aliases { get; set; } = [];
    public List<ProjectContextDocument> ContextDocuments { get; set; } = [];
    public ProjectDeadlineRule? DeadlineRule { get; set; }
}

/// <summary>プロジェクト名の表記ゆれを吸収する別名を表す。</summary>
public sealed class ProjectAlias
{
    // 別名の識別情報と利用日時を保持する。
    public long Identifier { get; set; }
    public string ProjectIdentifier { get; set; } = string.Empty;
    public string AliasText { get; set; } = string.Empty;
    public string NormalizedAlias { get; set; } = string.Empty;
    public string Source { get; set; } = TaskConstants.ScreenSource;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Codexへ渡す自由記述のプロジェクト背景情報を表す。</summary>
public sealed class ProjectContextDocument
{
    // 背景情報の本文、優先度、有効期間を保持する。
    public string Identifier { get; set; } = string.Empty;
    public string ProjectIdentifier { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ContentMarkdown { get; set; } = string.Empty;
    public int Priority { get; set; } = 3;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public string Source { get; set; } = TaskConstants.ScreenSource;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>プロジェクトの毎週の既定期限を表す。</summary>
public sealed class ProjectDeadlineRule
{
    // ISO曜日、ローカル時刻、期限種別を保持する。
    public string ProjectIdentifier { get; set; } = string.Empty;
    public int Weekday { get; set; } = 5;
    public string LocalTime { get; set; } = "17:00";
    public string DeadlineType { get; set; } = TaskConstants.TargetDeadlineType;
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>プロジェクト名候補の類似度を表す。</summary>
public sealed class ProjectResolutionCandidate
{
    // 候補プロジェクトと一致根拠を保持する。
    public string ProjectIdentifier { get; set; } = string.Empty;
    public string CanonicalName { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string MatchedText { get; set; } = string.Empty;
    public string MatchSource { get; set; } = string.Empty;
}

/// <summary>プロジェクト名の解決結果を表す。</summary>
public sealed class ProjectResolutionResult
{
    // 入力、解決状態、候補一覧を保持する。
    public string Reference { get; set; } = string.Empty;
    public string Status { get; set; } = ProjectConstants.NotFoundResolution;
    public string MatchType { get; set; } = string.Empty;
    public ProjectRecord? Project { get; set; }
    public List<ProjectResolutionCandidate> Candidates { get; set; } = [];
}

/// <summary>Codexがタスク判断前に取得するプロジェクト情報を表す。</summary>
public sealed class ProjectPreparationResult
{
    // 名前解決、次回期限、統合背景情報を保持する。
    public required ProjectResolutionResult Resolution { get; init; }
    public DateTimeOffset? NextDefaultDeadlineAt { get; init; }
    public string DefaultDeadlineType { get; init; } = TaskConstants.NoDeadlineType;
    public string ContextMarkdown { get; init; } = string.Empty;
    public bool ContextTruncated { get; init; }
}

/// <summary>プロジェクトの状態値と解決規則を定義する。</summary>
public static class ProjectConstants
{
    // DBとAPIで共有する固定値を保持する。
    public const string ActiveStatus = "有効";
    public const string ArchivedStatus = "アーカイブ";
    public const string ResolvedResolution = "resolved";
    public const string AmbiguousResolution = "ambiguous";
    public const string NotFoundResolution = "not_found";
    public const string AutomaticDeadlineOrigin = "auto";
    public const string ExplicitDeadlineOrigin = "explicit";
    public const string ProjectDefaultDeadlineOrigin = "project-default";
    public const string NoDeadlineOrigin = "none";
}

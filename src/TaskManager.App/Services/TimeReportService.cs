using TaskManager.Data;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>作業時間ログを日・週・月単位で集計する。</summary>
public sealed class TimeReportService(
    TimeEntryRepository timeEntryRepository,
    TaskRepository taskRepository,
    TimeProvider timeProvider)
{
    // 作業ログ、設定、現在時刻の取得元を保持する。
    private readonly TimeEntryRepository timeEntries = timeEntryRepository;
    private readonly TaskRepository tasks = taskRepository;
    private readonly TimeProvider clock = timeProvider;

    /// <summary>指定した期間と基準日から作業時間レポートを生成する。</summary>
    public async Task<TimeReportResult> GetReportAsync(
        string period,
        DateOnly? anchorDate,
        string? projectIdentifier,
        CancellationToken cancellationToken = default)
    {
        // 設定タイムゾーンで集計範囲と時間枠を決定する。
        string normalizedPeriod = period.Trim().ToLowerInvariant();
        if (normalizedPeriod is not ("day" or "week" or "month"))
        {
            throw new InvalidOperationException("periodはday、week、monthのいずれかを指定してください。");
        }

        TaskManagerSettings settings = await tasks.GetSettingsAsync(cancellationToken);
        TimeZoneInfo timeZone = ResolveTimeZone(settings.TimeZoneIdentifier);
        DateTimeOffset currentTime = clock.GetUtcNow();
        DateTimeOffset localCurrentTime = TimeZoneInfo.ConvertTime(currentTime, timeZone);
        DateOnly effectiveAnchor = anchorDate ?? DateOnly.FromDateTime(localCurrentTime.DateTime);
        (DateTimeOffset rangeStart, DateTimeOffset rangeEnd) = CalculateRange(normalizedPeriod, effectiveAnchor, timeZone);
        List<TimeEntryRecord> entries = await timeEntries.GetEntriesAsync(
            rangeStart,
            rangeEnd,
            projectIdentifier,
            includeVoided: false,
            cancellationToken: cancellationToken);

        // 実行中ログは現在時刻までとし、範囲外部分を切り詰めて集計する。
        List<EffectiveTimeEntry> effectiveEntries = entries
            .Select(entry => CreateEffectiveEntry(entry, rangeStart, rangeEnd, currentTime))
            .Where(entry => entry.EndAt > entry.StartAt)
            .OrderBy(entry => entry.StartAt)
            .ThenBy(entry => entry.Entry.Identifier, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<TimeReportBucket> buckets = BuildBuckets(
            normalizedPeriod,
            rangeStart,
            rangeEnd,
            timeZone,
            effectiveEntries);
        IReadOnlyList<TimeReportProjectSummary> projectSummaries = BuildProjectSummaries(effectiveEntries);
        IReadOnlyList<TimeReportTaskSummary> taskSummaries = BuildTaskSummaries(effectiveEntries);

        return new TimeReportResult
        {
            Period = normalizedPeriod,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd,
            GeneratedAt = currentTime,
            TotalSeconds = effectiveEntries.Sum(entry => CalculateSeconds(entry.StartAt, entry.EndAt)),
            ReviewCount = effectiveEntries.Count(entry => entry.Entry.NeedsReview),
            OverlapCount = CountOverlappingEntries(effectiveEntries),
            Buckets = buckets,
            Projects = projectSummaries,
            Tasks = taskSummaries
        };
    }

    /// <summary>設定値から利用可能なタイムゾーンを取得する。</summary>
    private static TimeZoneInfo ResolveTimeZone(string timeZoneIdentifier)
    {
        // 不正または空の識別子ではOSのローカルタイムゾーンへ戻す。
        if (string.IsNullOrWhiteSpace(timeZoneIdentifier))
        {
            return TimeZoneInfo.Local;
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneIdentifier);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>期間種別に対応する半開区間を計算する。</summary>
    private static (DateTimeOffset RangeStart, DateTimeOffset RangeEnd) CalculateRange(
        string period,
        DateOnly anchorDate,
        TimeZoneInfo timeZone)
    {
        // 週は月曜日始まり、月は暦月としてローカル日付を境界にする。
        DateOnly startDate = anchorDate;
        DateOnly endDate;
        if (period == "week")
        {
            int daysFromMonday = ((int)anchorDate.DayOfWeek + 6) % 7;
            startDate = anchorDate.AddDays(-daysFromMonday);
            endDate = startDate.AddDays(7);
        }
        else if (period == "month")
        {
            startDate = new DateOnly(anchorDate.Year, anchorDate.Month, 1);
            endDate = startDate.AddMonths(1);
        }
        else
        {
            endDate = startDate.AddDays(1);
        }
        return (CreateLocalBoundary(startDate, 0, timeZone), CreateLocalBoundary(endDate, 0, timeZone));
    }

    /// <summary>ローカル日付と時刻からオフセット付き境界を作成する。</summary>
    private static DateTimeOffset CreateLocalBoundary(DateOnly date, int hour, TimeZoneInfo timeZone)
    {
        // 集計境界のUTCオフセットを対象タイムゾーンから取得する。
        DateTime localDateTime = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        TimeSpan offset = timeZone.GetUtcOffset(localDateTime);
        return new DateTimeOffset(localDateTime, offset);
    }

    /// <summary>1件のログを集計範囲内へ切り詰める。</summary>
    private static EffectiveTimeEntry CreateEffectiveEntry(
        TimeEntryRecord entry,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset currentTime)
    {
        // 実行中の終了時刻は現在時刻とし、未来方向への集計を防ぐ。
        DateTimeOffset effectiveEnd = entry.EndAt ?? currentTime;
        DateTimeOffset clippedStart = entry.StartAt < rangeStart ? rangeStart : entry.StartAt;
        DateTimeOffset clippedEnd = effectiveEnd > rangeEnd ? rangeEnd : effectiveEnd;
        return new EffectiveTimeEntry(entry, clippedStart, clippedEnd);
    }

    /// <summary>表示用の1時間または1日ごとの時間枠を集計する。</summary>
    private static IReadOnlyList<TimeReportBucket> BuildBuckets(
        string period,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        TimeZoneInfo timeZone,
        IReadOnlyList<EffectiveTimeEntry> entries)
    {
        // 日表示だけ1時間単位、それ以外はローカル日付単位で分割する。
        List<(DateTimeOffset StartAt, DateTimeOffset EndAt)> ranges = [];
        if (period == "day")
        {
            DateOnly localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(rangeStart, timeZone).DateTime);
            for (int hour = 0; hour < 24; hour += 1)
            {
                ranges.Add((
                    CreateLocalBoundary(localDate, hour, timeZone),
                    hour == 23
                        ? CreateLocalBoundary(localDate.AddDays(1), 0, timeZone)
                        : CreateLocalBoundary(localDate, hour + 1, timeZone)));
            }
        }
        else
        {
            DateOnly currentDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(rangeStart, timeZone).DateTime);
            while (CreateLocalBoundary(currentDate, 0, timeZone) < rangeEnd)
            {
                ranges.Add((
                    CreateLocalBoundary(currentDate, 0, timeZone),
                    CreateLocalBoundary(currentDate.AddDays(1), 0, timeZone)));
                currentDate = currentDate.AddDays(1);
            }
        }

        return ranges.Select(range => BuildBucket(range.StartAt, range.EndAt, entries)).ToList();
    }

    /// <summary>1つの時間枠をプロジェクト別に集計する。</summary>
    private static TimeReportBucket BuildBucket(
        DateTimeOffset bucketStart,
        DateTimeOffset bucketEnd,
        IReadOnlyList<EffectiveTimeEntry> entries)
    {
        // 各ログを時間枠へ切り詰めて秒数を合計する。
        List<ProjectDurationSource> durationSources = entries
            .Where(entry => entry.StartAt < bucketEnd && entry.EndAt > bucketStart)
            .Select(entry => new ProjectDurationSource(
                GetProjectIdentifier(entry.Entry),
                GetProjectName(entry.Entry),
                CalculateSeconds(
                    entry.StartAt < bucketStart ? bucketStart : entry.StartAt,
                    entry.EndAt > bucketEnd ? bucketEnd : entry.EndAt)))
            .Where(source => source.TotalSeconds > 0)
            .ToList();
        IReadOnlyList<TimeReportProjectDuration> projects = durationSources
            .GroupBy(source => new { source.ProjectIdentifier, source.ProjectName })
            .Select(group => new TimeReportProjectDuration
            {
                ProjectIdentifier = group.Key.ProjectIdentifier,
                ProjectName = group.Key.ProjectName,
                TotalSeconds = group.Sum(source => source.TotalSeconds)
            })
            .OrderByDescending(project => project.TotalSeconds)
            .ThenBy(project => project.ProjectName, StringComparer.CurrentCulture)
            .ToList();
        return new TimeReportBucket
        {
            StartAt = bucketStart,
            EndAt = bucketEnd,
            TotalSeconds = projects.Sum(project => project.TotalSeconds),
            Projects = projects
        };
    }

    /// <summary>期間全体をプロジェクト別に集計する。</summary>
    private static IReadOnlyList<TimeReportProjectSummary> BuildProjectSummaries(
        IReadOnlyList<EffectiveTimeEntry> entries)
    {
        // プロジェクト未割当も1つの集計行として残す。
        return entries
            .GroupBy(entry => new
            {
                ProjectIdentifier = GetProjectIdentifier(entry.Entry),
                ProjectName = GetProjectName(entry.Entry)
            })
            .Select(group => new TimeReportProjectSummary
            {
                ProjectIdentifier = group.Key.ProjectIdentifier,
                ProjectName = group.Key.ProjectName,
                TotalSeconds = group.Sum(entry => CalculateSeconds(entry.StartAt, entry.EndAt)),
                EntryCount = group.Count()
            })
            .OrderByDescending(summary => summary.TotalSeconds)
            .ThenBy(summary => summary.ProjectName, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>期間全体をタスクまたは自由活動別に集計する。</summary>
    private static IReadOnlyList<TimeReportTaskSummary> BuildTaskSummaries(
        IReadOnlyList<EffectiveTimeEntry> entries)
    {
        // 削除済みタスクや自由活動は名称スナップショットで区別する。
        return entries
            .GroupBy(entry => new
            {
                TaskIdentifier = entry.Entry.TaskIdentifier ?? TimeTrackingConstants.FreeActivityTaskIdentifier,
                TaskTitle = entry.Entry.Title,
                ProjectIdentifier = GetProjectIdentifier(entry.Entry),
                ProjectName = GetProjectName(entry.Entry)
            })
            .Select(group => new TimeReportTaskSummary
            {
                TaskIdentifier = group.Key.TaskIdentifier,
                TaskTitle = group.Key.TaskTitle,
                ProjectIdentifier = group.Key.ProjectIdentifier,
                ProjectName = group.Key.ProjectName,
                TotalSeconds = group.Sum(entry => CalculateSeconds(entry.StartAt, entry.EndAt)),
                EntryCount = group.Count()
            })
            .OrderByDescending(summary => summary.TotalSeconds)
            .ThenBy(summary => summary.TaskTitle, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>同一期間に時間が重なるログの件数を数える。</summary>
    private static int CountOverlappingEntries(IReadOnlyList<EffectiveTimeEntry> entries)
    {
        // 開始時刻順に走査し、それ以前の最大終了時刻を越えないログを重複と数える。
        int overlapCount = 0;
        DateTimeOffset? maximumEnd = null;
        foreach (EffectiveTimeEntry entry in entries)
        {
            if (maximumEnd.HasValue && entry.StartAt < maximumEnd.Value)
            {
                overlapCount += 1;
            }
            if (!maximumEnd.HasValue || entry.EndAt > maximumEnd.Value)
            {
                maximumEnd = entry.EndAt;
            }
        }
        return overlapCount;
    }

    /// <summary>プロジェクト未割当を含む集計用識別子を返す。</summary>
    private static string GetProjectIdentifier(TimeEntryRecord entry)
    {
        // NULLや空文字は固定の未割当識別子へ統一する。
        return string.IsNullOrWhiteSpace(entry.ProjectIdentifier)
            ? TimeTrackingConstants.UnassignedProjectIdentifier
            : entry.ProjectIdentifier;
    }

    /// <summary>プロジェクト未割当を含む集計用表示名を返す。</summary>
    private static string GetProjectName(TimeEntryRecord entry)
    {
        // 保存時点の名称がないログは未割当として表示する。
        return string.IsNullOrWhiteSpace(entry.ProjectNameSnapshot)
            ? "未割当"
            : entry.ProjectNameSnapshot;
    }

    /// <summary>半開区間の秒数を負数にならない形で返す。</summary>
    private static long CalculateSeconds(DateTimeOffset startAt, DateTimeOffset endAt)
    {
        // 秒未満を切り捨て、逆転区間は0秒とする。
        return Math.Max(0L, (long)Math.Floor((endAt - startAt).TotalSeconds));
    }

    /// <summary>集計範囲内へ切り詰めたログを保持する。</summary>
    private sealed record EffectiveTimeEntry(
        TimeEntryRecord Entry,
        DateTimeOffset StartAt,
        DateTimeOffset EndAt);

    /// <summary>時間枠のプロジェクト別秒数を一時保持する。</summary>
    private sealed record ProjectDurationSource(
        string ProjectIdentifier,
        string ProjectName,
        long TotalSeconds);
}

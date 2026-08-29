using System.Globalization;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>活動時間と予定から作業可能時間を計算する。</summary>
public sealed class CalendarAvailabilityService
{
    /// <summary>現在から次の予定までの連続した空き時間を計算する。</summary>
    public AvailableSlot CalculateCurrentAvailableSlot(
        DateTimeOffset currentTime,
        TaskManagerSettings settings,
        IReadOnlyList<CalendarEventRecord> calendarEvents)
    {
        // 活動時間外、予定中、予定前の空き時間を区別する。
        (DateTimeOffset ActivityStart, DateTimeOffset ActivityEnd) activityWindow = CreateActivityWindow(currentTime, settings);
        if (currentTime < activityWindow.ActivityStart)
        {
            return new AvailableSlot(0, "活動開始時刻前です。", string.Empty, string.Empty, activityWindow.ActivityStart);
        }
        if (currentTime >= activityWindow.ActivityEnd)
        {
            return new AvailableSlot(0, "活動終了時刻を過ぎています。", string.Empty, string.Empty, activityWindow.ActivityEnd);
        }

        List<BusyInterval> busyIntervals = BuildBusyIntervals(
            activityWindow.ActivityStart,
            activityWindow.ActivityEnd,
            settings,
            calendarEvents);
        BusyInterval? currentInterval = busyIntervals.FirstOrDefault(interval => currentTime >= interval.StartAt && currentTime < interval.EndAt);
        if (currentInterval is not null)
        {
            return new AvailableSlot(
                0,
                $"現在カレンダー予定中です: {DefaultTitle(currentInterval.Title)}",
                currentInterval.Title,
                currentInterval.Location,
                currentInterval.EndAt);
        }

        BusyInterval? nextInterval = busyIntervals.FirstOrDefault(interval => interval.StartAt > currentTime);
        DateTimeOffset slotEnd = nextInterval?.StartAt ?? activityWindow.ActivityEnd;
        // 現在時刻から空き時間の終了までを分へ換算する。
        int availableMinutes = Math.Max(0, (int)Math.Floor((slotEnd - currentTime).TotalMinutes));
        return new AvailableSlot(
            availableMinutes,
            string.Empty,
            nextInterval?.Title ?? string.Empty,
            nextInterval?.Location ?? string.Empty,
            slotEnd);
    }

    /// <summary>現在から期限までに利用できる合計作業時間を計算する。</summary>
    public double CalculateAvailableMinutesUntil(
        DateTimeOffset currentTime,
        DateTimeOffset deadline,
        TaskManagerSettings settings,
        IReadOnlyList<CalendarEventRecord> calendarEvents)
    {
        // 14日以内は予定を控除し、それ以降は日次容量で概算する。
        if (deadline <= currentTime)
        {
            return 0;
        }
        DateTimeOffset cacheHorizonEnd = StartOfDay(currentTime).AddDays(settings.CalendarLookaheadDays + 1);
        DateTimeOffset exactRangeEnd = deadline < cacheHorizonEnd ? deadline : cacheHorizonEnd;
        double availableMinutes = 0;
        DateTimeOffset processingDate = StartOfDay(currentTime);
        while (processingDate < exactRangeEnd)
        {
            (DateTimeOffset ActivityStart, DateTimeOffset ActivityEnd) activityWindow = CreateActivityWindow(processingDate, settings);
            DateTimeOffset intervalStart = activityWindow.ActivityStart > currentTime ? activityWindow.ActivityStart : currentTime;
            DateTimeOffset intervalEnd = activityWindow.ActivityEnd < exactRangeEnd ? activityWindow.ActivityEnd : exactRangeEnd;
            if (intervalEnd > intervalStart)
            {
                availableMinutes += CalculateFreeMinutes(intervalStart, intervalEnd, settings, calendarEvents);
            }
            processingDate = processingDate.AddDays(1);
        }
        if (deadline > cacheHorizonEnd)
        {
            // キャッシュ範囲外の日数へ設定済みの日次作業可能時間を加算する。
            int remainingDays = Math.Max(0, (int)Math.Ceiling((deadline - cacheHorizonEnd).TotalDays));
            availableMinutes += remainingDays * settings.FutureDailyCapacityMinutes;
        }
        return Math.Max(0, Math.Floor(availableMinutes));
    }

    /// <summary>指定時間帯から予定を控除した空き時間を計算する。</summary>
    private static double CalculateFreeMinutes(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TaskManagerSettings settings,
        IReadOnlyList<CalendarEventRecord> calendarEvents)
    {
        // 重複予定を統合した後に合計予定時間を控除する。
        List<BusyInterval> busyIntervals = BuildBusyIntervals(windowStart, windowEnd, settings, calendarEvents);
        double busyMinutes = 0;
        foreach (BusyInterval busyInterval in busyIntervals)
        {
            DateTimeOffset clippedStart = busyInterval.StartAt > windowStart ? busyInterval.StartAt : windowStart;
            DateTimeOffset clippedEnd = busyInterval.EndAt < windowEnd ? busyInterval.EndAt : windowEnd;
            if (clippedEnd > clippedStart)
            {
                busyMinutes += (clippedEnd - clippedStart).TotalMinutes;
            }
        }
        return Math.Max(0, (windowEnd - windowStart).TotalMinutes - busyMinutes);
    }

    /// <summary>対象期間と重なる予定を余白付き区間へ変換して統合する。</summary>
    private static List<BusyInterval> BuildBusyIntervals(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TaskManagerSettings settings,
        IReadOnlyList<CalendarEventRecord> calendarEvents)
    {
        // 空き予定と設定上無視する終日予定を除外する。
        List<BusyInterval> intervals = calendarEvents
            .Where(calendarEvent => calendarEvent.IsBusy)
            .Where(calendarEvent => !calendarEvent.IsAllDay || settings.TreatAllDayAsBusy)
            .Select(calendarEvent => new BusyInterval(
                calendarEvent.StartAt.AddMinutes(-settings.CalendarBufferMinutes),
                calendarEvent.EndAt.AddMinutes(settings.CalendarBufferMinutes),
                calendarEvent.Title,
                calendarEvent.Location))
            .Where(interval => interval.EndAt > windowStart && interval.StartAt < windowEnd)
            .OrderBy(interval => interval.StartAt)
            .ThenBy(interval => interval.EndAt)
            .ToList();
        if (intervals.Count == 0)
        {
            return [];
        }
        List<BusyInterval> mergedIntervals = [intervals[0]];
        foreach (BusyInterval currentInterval in intervals.Skip(1))
        {
            BusyInterval latestInterval = mergedIntervals[^1];
            if (currentInterval.StartAt <= latestInterval.EndAt)
            {
                mergedIntervals[^1] = latestInterval with
                {
                    EndAt = currentInterval.EndAt > latestInterval.EndAt ? currentInterval.EndAt : latestInterval.EndAt
                };
            }
            else
            {
                mergedIntervals.Add(currentInterval);
            }
        }
        return mergedIntervals;
    }

    /// <summary>指定日の活動開始時刻と終了時刻を作成する。</summary>
    private static (DateTimeOffset ActivityStart, DateTimeOffset ActivityEnd) CreateActivityWindow(
        DateTimeOffset baseDate,
        TaskManagerSettings settings)
    {
        // 設定時刻を同じ日付とオフセットへ組み立てる。
        return (
            CreateDateAtClock(baseDate, settings.ActivityStart),
            CreateDateAtClock(baseDate, settings.ActivityEnd));
    }

    /// <summary>指定日の時刻文字列をDateTimeOffsetへ変換する。</summary>
    private static DateTimeOffset CreateDateAtClock(DateTimeOffset baseDate, string clockText)
    {
        // 不正な時刻は午前0時として扱う。
        TimeSpan clock = TimeSpan.TryParseExact(clockText, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan parsedClock)
            ? parsedClock
            : TimeSpan.Zero;
        DateTimeOffset startOfDay = StartOfDay(baseDate);
        return startOfDay.Add(clock);
    }

    /// <summary>指定日時と同じオフセットの午前0時を作成する。</summary>
    private static DateTimeOffset StartOfDay(DateTimeOffset baseDate)
    {
        // 時分秒を0へ揃える。
        return new DateTimeOffset(baseDate.Year, baseDate.Month, baseDate.Day, 0, 0, 0, baseDate.Offset);
    }

    /// <summary>空の予定名へ既定文字列を補う。</summary>
    private static string DefaultTitle(string title)
    {
        // 画面の理由文が空にならないようにする。
        return string.IsNullOrWhiteSpace(title) ? "予定" : title;
    }

    /// <summary>余白適用後の予定区間を表す。</summary>
    private sealed record BusyInterval(DateTimeOffset StartAt, DateTimeOffset EndAt, string Title, string Location);
}

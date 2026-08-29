using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using System.Text.Json;
using TaskManager.Configuration;
using TaskManager.Data;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>Google Calendarを読取専用で同期する。</summary>
public sealed class GoogleCalendarService(
    TaskManagerPaths taskManagerPaths,
    TaskRepository taskRepository,
    DpapiDataStore dpapiDataStore,
    TimeProvider timeProvider,
    ILogger<GoogleCalendarService> logger)
{
    // 認証情報、保存層、時刻、ログの依存先を保持する。
    private readonly TaskManagerPaths paths = taskManagerPaths;
    private readonly TaskRepository repository = taskRepository;
    private readonly DpapiDataStore tokenStore = dpapiDataStore;
    private readonly TimeProvider clock = timeProvider;
    private readonly ILogger<GoogleCalendarService> applicationLogger = logger;
    private readonly SemaphoreSlim synchronizationLock = new(1, 1);
    private const string GoogleUserKey = "task-manager-user";
    private static readonly string[] CalendarScopes = [CalendarService.Scope.CalendarReadonly];

    /// <summary>バックグラウンド同期に必要な認証情報とトークンが揃っているか返す。</summary>
    public bool IsBackgroundSynchronizationReady()
    {
        // 未設定時に5分ごとのエラー履歴を発生させないよう事前判定する。
        return File.Exists(paths.CredentialPath)
            && Directory.Exists(paths.TokenDirectory)
            && Directory.EnumerateFiles(paths.TokenDirectory, "*.bin").Any();
    }

    /// <summary>アップロードされたデスクトップOAuth認証情報を保存する。</summary>
    public async Task SaveCredentialsAsync(Stream credentialStream, CancellationToken cancellationToken = default)
    {
        // JSONとして解析できることを確認してから所定位置へ保存する。
        using JsonDocument credentialDocument = await JsonDocument.ParseAsync(credentialStream, cancellationToken: cancellationToken);
        if (!credentialDocument.RootElement.TryGetProperty("installed", out JsonElement installedElement)
            || !installedElement.TryGetProperty("client_id", out _))
        {
            throw new InvalidOperationException("デスクトップアプリ用のcredentials.jsonではありません。");
        }
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.CredentialPath, credentialDocument.RootElement.GetRawText(), cancellationToken);
    }

    /// <summary>システムブラウザを利用してGoogle認証を開始する。</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // デスクトップOAuthフローで読取専用権限だけを要求する。
        EnsureCredentialFile();
        GoogleClientSecrets clientSecrets = await GoogleClientSecrets.FromFileAsync(paths.CredentialPath, cancellationToken);
        await GoogleWebAuthorizationBroker.AuthorizeAsync(
            clientSecrets.Secrets,
            CalendarScopes,
            GoogleUserKey,
            cancellationToken,
            tokenStore);
    }

    /// <summary>設定済みカレンダーの予定を14日分同期する。</summary>
    public async Task<int> SynchronizeAsync(bool allowInteractiveAuthorization, CancellationToken cancellationToken = default)
    {
        // 同時同期を防ぎ、成功時だけキャッシュを置き換える。
        await synchronizationLock.WaitAsync(cancellationToken);
        try
        {
            EnsureCredentialFile();
            if (!allowInteractiveAuthorization && !Directory.EnumerateFiles(paths.TokenDirectory, "*.bin").Any())
            {
                throw new InvalidOperationException("Google Calendarが未認証です。設定画面から接続してください。");
            }
            GoogleClientSecrets clientSecrets = await GoogleClientSecrets.FromFileAsync(paths.CredentialPath, cancellationToken);
            UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                clientSecrets.Secrets,
                CalendarScopes,
                GoogleUserKey,
                cancellationToken,
                tokenStore);
            using CalendarService calendarService = new(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Local Task Manager"
            });
            TaskManagerSettings settings = await repository.GetSettingsAsync(cancellationToken);
            DateTimeOffset currentTime = clock.GetLocalNow();
            DateTimeOffset rangeStart = new(currentTime.Year, currentTime.Month, currentTime.Day, 0, 0, 0, currentTime.Offset);
            DateTimeOffset rangeEnd = rangeStart.AddDays(settings.CalendarLookaheadDays + 1);
            List<CalendarEventRecord> synchronizedEvents = [];
            string[] calendarIdentifiers = settings.CalendarIdentifiers
                .Split([',', '、', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string calendarIdentifier in calendarIdentifiers.DefaultIfEmpty("primary"))
            {
                EventsResource.ListRequest listRequest = calendarService.Events.List(calendarIdentifier);
                listRequest.TimeMinDateTimeOffset = rangeStart;
                listRequest.TimeMaxDateTimeOffset = rangeEnd;
                listRequest.SingleEvents = true;
                listRequest.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                Events eventsPage = await listRequest.ExecuteAsync(cancellationToken);
                synchronizedEvents.AddRange(eventsPage.Items.Select(calendarEvent => ConvertEvent(calendarIdentifier, calendarEvent, currentTime.Offset)));
            }
            await repository.ReplaceCalendarEventsAsync(synchronizedEvents, currentTime, cancellationToken);
            return synchronizedEvents.Count;
        }
        catch (Exception synchronizationError)
        {
            applicationLogger.LogWarning(synchronizationError, "Google Calendar synchronization failed.");
            await repository.SaveCalendarErrorAsync(synchronizationError.Message, cancellationToken);
            throw;
        }
        finally
        {
            synchronizationLock.Release();
        }
    }

    /// <summary>Google予定を内部キャッシュ形式へ変換する。</summary>
    private static CalendarEventRecord ConvertEvent(string calendarIdentifier, Event calendarEvent, TimeSpan localOffset)
    {
        // 日時指定と終日指定を区別して正しい期間を作る。
        bool isAllDay = string.IsNullOrWhiteSpace(calendarEvent.Start.DateTimeRaw);
        DateTimeOffset startAt = isAllDay
            ? ParseAllDay(calendarEvent.Start.Date, localOffset)
            : calendarEvent.Start.DateTimeDateTimeOffset ?? DateTimeOffset.Parse(calendarEvent.Start.DateTimeRaw);
        DateTimeOffset endAt = isAllDay
            ? ParseAllDay(calendarEvent.End.Date, localOffset)
            : calendarEvent.End.DateTimeDateTimeOffset ?? DateTimeOffset.Parse(calendarEvent.End.DateTimeRaw);
        return new CalendarEventRecord
        {
            EventIdentifier = calendarEvent.Id,
            CalendarIdentifier = calendarIdentifier,
            Title = calendarEvent.Summary ?? string.Empty,
            Description = (calendarEvent.Description ?? string.Empty)[..Math.Min(calendarEvent.Description?.Length ?? 0, 1000)],
            StartAt = startAt,
            EndAt = endAt,
            Location = calendarEvent.Location ?? string.Empty,
            IsAllDay = isAllDay,
            IsBusy = !string.Equals(calendarEvent.Transparency, "transparent", StringComparison.OrdinalIgnoreCase),
            ExternalUpdatedAt = calendarEvent.UpdatedDateTimeOffset
        };
    }

    /// <summary>Googleの終日日付をローカル日時へ変換する。</summary>
    private static DateTimeOffset ParseAllDay(string? dateText, TimeSpan localOffset)
    {
        // 日付だけの値へ現在のローカルオフセットを付与する。
        if (!DateOnly.TryParse(dateText, out DateOnly parsedDate))
        {
            throw new InvalidOperationException($"予定の日付を解析できません: {dateText}");
        }
        return new DateTimeOffset(parsedDate.Year, parsedDate.Month, parsedDate.Day, 0, 0, 0, localOffset);
    }

    /// <summary>Google OAuth認証情報ファイルの存在を確認する。</summary>
    private void EnsureCredentialFile()
    {
        // 設定手順が分かるエラーを返す。
        if (!File.Exists(paths.CredentialPath))
        {
            throw new FileNotFoundException("設定画面からGoogleのcredentials.jsonを登録してください。", paths.CredentialPath);
        }
    }
}

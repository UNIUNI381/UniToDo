using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using TaskManager.Api;
using TaskManager.Configuration;
using TaskManager.Data;
using TaskManager.Domain;
using TaskManager.Services;
using TaskManager.Windows;

namespace TaskManager;

/// <summary>ローカルタスク管理アプリの起動処理を提供する。</summary>
public static class Program
{
    // 同じWindowsユーザーでの多重起動を防ぐ名前を保持する。
    private const string SingleInstanceMutexName = "LocalTaskManager.48120.Singleton";
    // 同じWindowsユーザーで監視親が重複することを防ぐ名前を保持する。
    private const string WatchdogMutexName = "LocalTaskManager.48120.Watchdog.Singleton";
    // 監視プロセスが短時間に再起動を繰り返す上限を保持する。
    private const int MaximumConsecutiveRestartCount = 3;
    // 安定稼働とみなして連続異常終了回数を戻す時間を保持する。
    private static readonly TimeSpan StableRuntime = TimeSpan.FromMinutes(5);
    // 異常終了後にファイルやポートが解放されるまで待つ時間を保持する。
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(3);

    /// <summary>Webサーバー、常駐処理、トレイ画面を起動する。</summary>
    [STAThread]
    public static async Task<int> Main(string[] arguments)
    {
        // 監視親とアプリ本体のどちらでも、メイン処理外の未処理例外をログへ残す。
        bool isWatchdog = arguments.Contains("--watchdog", StringComparer.OrdinalIgnoreCase);
        RegisterGlobalExceptionLogging(isWatchdog);
        try
        {
            // スタートアップ起動では子プロセスを監視し、通常起動ではアプリ本体を実行する。
            if (isWatchdog)
            {
                return await RunWatchdogAsync();
            }
            return await RunApplicationAsync(arguments);
        }
        catch (Exception fatalError)
        {
            string logCategory = isWatchdog ? "watchdog-crash" : "crash";
            string errorMessage = isWatchdog
                ? "監視プロセスが未処理例外で終了しました。"
                : "アプリ本体が未処理例外で終了しました。";
            await WriteRuntimeLogAsync(logCategory, errorMessage, fatalError);
            if (isWatchdog)
            {
                ShowRestartFailureDialog();
            }
            return 1;
        }
    }

    /// <summary>メイン処理外で発生した未処理例外をクラッシュログへ記録する。</summary>
    private static void RegisterGlobalExceptionLogging(bool isWatchdog)
    {
        // 実行形態ごとにログ名を分け、監視親の障害とアプリ本体の障害を区別する。
        string logCategory = isWatchdog ? "watchdog-crash" : "crash";
        AppDomain.CurrentDomain.UnhandledException += (_, eventArguments) =>
        {
            Exception? unhandledError = eventArguments.ExceptionObject as Exception;
            WriteRuntimeLogSynchronously(
                logCategory,
                "未処理例外を検出しました。",
                unhandledError);
        };
        TaskScheduler.UnobservedTaskException += (_, eventArguments) =>
        {
            WriteRuntimeLogSynchronously(
                logCategory,
                "未監視の非同期例外を検出しました。",
                eventArguments.Exception);
            eventArguments.SetObserved();
        };

        // WinFormsスレッドの例外を記録し、トレイ機能だけの障害で本体全体を終了させない。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArguments) =>
            WriteRuntimeLogSynchronously(
                logCategory,
                "Windows画面スレッドの例外を検出しました。",
                eventArguments.Exception);
    }

    /// <summary>終了処理中でも完了を待って障害ログを保存する。</summary>
    private static void WriteRuntimeLogSynchronously(
        string logCategory,
        string message,
        Exception? exception)
    {
        // 例外通知元へ追加の例外を返さないログ関数を同期的に完了させる。
        try
        {
            WriteRuntimeLogAsync(logCategory, message, exception).GetAwaiter().GetResult();
        }
        catch
        {
            // 障害ログの保存失敗で元の例外処理を妨げない。
        }
    }

    /// <summary>Webサーバー、常駐処理、トレイ画面の本体を実行する。</summary>
    private static async Task<int> RunApplicationAsync(string[] arguments)
    {
        // 既に起動中なら必要に応じて既存画面だけを開く。
        using Mutex singleInstanceMutex = new(true, SingleInstanceMutexName, out bool mutexCreated);
        if (!mutexCreated)
        {
            if (!arguments.Contains("--background", StringComparer.OrdinalIgnoreCase))
            {
                OpenDashboard();
            }
            return 0;
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = arguments,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls(TaskManagerPaths.GetLocalAddress());
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            // WebとCLIで同じcamelCase JSONを利用する。
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.SerializerOptions.WriteIndented = false;
        });
        RegisterServices(builder.Services);

        WebApplication application = builder.Build();
        application.UseMiddleware<LocalRequestMiddleware>();
        application.UseMiddleware<UiChangeNotificationMiddleware>();
        application.UseDefaultFiles();
        application.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ConfigureStaticFileResponse });
        application.MapTaskManagerApi();
        application.MapCodexChat();
        application.MapFallbackToFile("index.html");

        DatabaseInitializer databaseInitializer = application.Services.GetRequiredService<DatabaseInitializer>();
        await databaseInitializer.InitializeAsync();
        TaskService taskService = application.Services.GetRequiredService<TaskService>();
        await taskService.ReconcileTimeTrackingAsync();
        TaskManagerTray tray = application.Services.GetRequiredService<TaskManagerTray>();
        tray.Start();
        await application.StartAsync();
        SystemIncidentService systemIncidentService = application.Services.GetRequiredService<SystemIncidentService>();
        SystemIncidentRecord? recoveredIncident = await systemIncidentService.MarkRecoveredAsync();
        if (recoveredIncident is not null)
        {
            // 重要通知設定にかかわらず、自動復旧をユーザーへ一度だけ知らせる。
            tray.Notify(
                $"{TaskConstants.ApplicationDisplayName}を自動復旧しました",
                "異常終了を検出しました。ダッシュボードでエラー情報を確認し、必要に応じてCodexへ共有してください。");
        }
        if (!arguments.Contains("--background", StringComparer.OrdinalIgnoreCase))
        {
            OpenDashboard();
        }
        await application.WaitForShutdownAsync();
        return 0;
    }

    /// <summary>アプリ本体の異常終了を検出し、制限回数内で自動再起動する。</summary>
    private static async Task<int> RunWatchdogAsync()
    {
        // タスクスケジューラと手動起動が重なっても監視親を一つだけ維持する。
        using Mutex watchdogMutex = new(true, WatchdogMutexName, out bool mutexCreated);
        if (!mutexCreated)
        {
            await WriteRuntimeLogAsync("watchdog", "既存の監視プロセスを検出したため重複起動を終了します。");
            return 0;
        }

        // 正常終了は利用者による終了として監視も終了し、異常終了だけを再起動する。
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("TaskManager実行ファイルの場所を取得できません。");
        int consecutiveRestartCount = 0;
        await WriteRuntimeLogAsync(
            "watchdog",
            $"監視プロセスを開始しました。監視PID={Environment.ProcessId}");
        while (true)
        {
            DateTimeOffset processStartedAt = DateTimeOffset.Now;
            using ChildProcessJob childProcessJob = new();
            using Process applicationProcess = StartManagedApplication(executablePath);
            childProcessJob.Assign(applicationProcess);
            await WriteRuntimeLogAsync(
                "watchdog",
                $"アプリ本体を開始しました。本体PID={applicationProcess.Id}");
            await applicationProcess.WaitForExitAsync();
            TimeSpan processRuntime = DateTimeOffset.Now - processStartedAt;
            await WriteRuntimeLogAsync(
                "watchdog",
                $"アプリ本体が終了しました。終了コード={applicationProcess.ExitCode} 稼働秒数={processRuntime.TotalSeconds:F1}");
            if (applicationProcess.ExitCode == 0)
            {
                await WriteRuntimeLogAsync("watchdog", "正常終了を検出したため監視プロセスを終了します。");
                return 0;
            }

            // 5分以上動作した場合は過去の異常終了を連続回数へ含めない。
            if (processRuntime >= StableRuntime)
            {
                consecutiveRestartCount = 0;
            }
            consecutiveRestartCount++;
            await WriteRuntimeLogAsync(
                "recovery",
                $"アプリ本体が終了コード{applicationProcess.ExitCode}で停止しました。再起動回数={consecutiveRestartCount}");
            try
            {
                SystemIncidentService systemIncidentService = new(new TaskManagerPaths(), TimeProvider.System);
                await systemIncidentService.RecordFailureAsync(applicationProcess.ExitCode, consecutiveRestartCount);
            }
            catch (Exception incidentError)
            {
                // 障害通知の保存失敗だけで監視と自動再起動を停止させない。
                await WriteRuntimeLogAsync(
                    "watchdog-crash",
                    "異常終了情報の保存に失敗しました。自動再起動は継続します。",
                    incidentError);
            }
            if (consecutiveRestartCount >= MaximumConsecutiveRestartCount)
            {
                await WriteRuntimeLogAsync("recovery", "短時間の連続異常終了を検出したため自動再起動を停止しました。");
                ShowRestartFailureDialog();
                // 意図したサーキットブレーカー停止を成功終了として外側のタスクスケジューラへ伝える。
                return 0;
            }
            await Task.Delay(RestartDelay);
        }
    }

    /// <summary>監視対象となるバックグラウンドのアプリ本体を起動する。</summary>
    private static Process StartManagedApplication(string executablePath)
    {
        // シェルを介さず同じ配布物を非表示で起動し、終了コードを監視できるようにする。
        ProcessStartInfo processStartInfo = new(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        processStartInfo.ArgumentList.Add("--background");
        processStartInfo.ArgumentList.Add("--watchdog-child");
        return Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("TaskManager本体を監視プロセスから起動できませんでした。");
    }

    /// <summary>クラッシュまたは自動復旧の情報をユーザーデータ領域へ追記する。</summary>
    private static async Task WriteRuntimeLogAsync(string logCategory, string message, Exception? exception = null)
    {
        // 次回調査で終了原因を復元できるよう、日別UTF-8ログへ時刻と例外全文を残す。
        try
        {
            TaskManagerPaths applicationPaths = new();
            applicationPaths.EnsureDirectories();
            string logPath = Path.Combine(
                applicationPaths.LogDirectory,
                $"{logCategory}-{DateTimeOffset.Now:yyyy-MM-dd}.log");
            StringBuilder logText = new();
            logText.Append(DateTimeOffset.Now.ToString("O"));
            logText.Append(' ');
            logText.AppendLine(message);
            if (exception is not null)
            {
                logText.AppendLine(exception.ToString());
            }
            await File.AppendAllTextAsync(logPath, logText.ToString(), Encoding.UTF8);
        }
        catch
        {
            // ログ保存失敗で元の終了処理を妨げない。
        }
    }

    /// <summary>連続異常終了で自動復旧できないことをWindowsダイアログで通知する。</summary>
    private static void ShowRestartFailureDialog()
    {
        // Webサーバーが起動できない状態でもユーザーが障害へ気づける表示手段を確保する。
        System.Windows.Forms.MessageBox.Show(
            $"{TaskConstants.ApplicationDisplayName}の起動に3回連続で失敗しました。\n"
            + "ログフォルダーを確認し、Codexへ修正を依頼してください。\n\n"
            + "%LOCALAPPDATA%\\TaskManager\\logs",
            $"{TaskConstants.ApplicationDisplayName}を起動できません",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    /// <summary>アプリケーションサービスを依存性注入へ登録する。</summary>
    private static void RegisterServices(IServiceCollection services)
    {
        // 状態を共有するサービスは単一インスタンスとして登録する。
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<TaskManagerPaths>();
        services.AddSingleton(provider => RemoteAccessSettings.Load(
            Path.Combine(provider.GetRequiredService<TaskManagerPaths>().DataDirectory, "remote-access.json"),
            provider.GetRequiredService<ILogger<RemoteAccessSettings>>()));
        services.AddSingleton<SystemIncidentService>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<TaskRepository>();
        services.AddSingleton<ExternalBackupService>();
        services.AddSingleton<TimeEntryRepository>();
        services.AddSingleton<ProjectRepository>();
        services.AddSingleton<CalendarAvailabilityService>();
        services.AddSingleton<TaskValidationService>();
        services.AddSingleton<RecommendationService>();
        services.AddSingleton<UiChangeNotifier>();
        services.AddSingleton<ProjectService>();
        services.AddSingleton<IDraftPostProcessingService, DraftPostProcessingService>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<TimeReportService>();
        services.AddSingleton<DpapiDataStore>();
        services.AddSingleton<GoogleCalendarService>();
        services.AddSingleton<CalendarSynchronizationQueue>();
        services.AddSingleton<CodexReviewService>();
        services.AddSingleton<CodexSubmissionQueue>();
        services.AddSingleton<CodexDesktopLauncher>();
        services.AddSingleton<ICodexAppServer, CodexAppServer>();
        services.AddSingleton<CodexChatService>();
        services.AddSingleton<ICodexCommandRunner, CodexVoiceChatRunner>();
        services.AddSingleton(new VoiceInputCoordinatorOptions());
        services.AddHttpClient<TypeWhisperApiClient>(client =>
        {
            // TypeWhisperのループバックAPIを既定ポートで構成し、起動後はディスカバリー情報へ追従する。
            client.BaseAddress = new Uri("http://127.0.0.1:8978/");
            // 録音開始API内で大容量認識モデルを遅延ロードできるよう十分な上限を確保する。
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        services.AddHttpClient<IVoiceInputRuntime, VoiceInputRuntime>(client =>
        {
            // OllamaのループバックAPIでコールド時のモデル読込みまで待てるようにする。
            client.BaseAddress = new Uri("http://127.0.0.1:11434/");
            client.Timeout = TimeSpan.FromSeconds(45);
        });
        services.AddSingleton<VoiceInputCoordinator>();
        services.AddSingleton<TaskManagerTray>();
        services.AddSingleton<IUserNotificationService>(serviceProvider => serviceProvider.GetRequiredService<TaskManagerTray>());
        services.AddHostedService<AutomationWorker>();
        services.AddHostedService<CalendarSynchronizationWorker>();
        services.AddHostedService<CodexSubmissionWorker>();
    }

    /// <summary>既定ブラウザでローカルダッシュボードを開く。</summary>
    private static void OpenDashboard()
    {
        // シェルへlocalhost URLを渡して利用者の既定ブラウザを尊重する。
        Process.Start(new ProcessStartInfo(TaskManagerPaths.GetLocalAddress()) { UseShellExecute = true });
    }

    /// <summary>ローカル画面の静的資産をブラウザへキャッシュさせない。</summary>
    private static void ConfigureStaticFileResponse(StaticFileResponseContext responseContext)
    {
        // 内蔵ブラウザでも更新後のHTML、JavaScript、CSSを必ず読み直すよう応答を指定する。
        responseContext.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        responseContext.Context.Response.Headers["Pragma"] = "no-cache";
        responseContext.Context.Response.Headers["Expires"] = "0";
    }
}

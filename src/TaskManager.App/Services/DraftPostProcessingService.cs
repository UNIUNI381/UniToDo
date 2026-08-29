using TaskManager.Data;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>下書き保存後の検証と推薦更新を抽象化する。</summary>
public interface IDraftPostProcessingService
{
    /// <summary>保存済み下書きを検証して推薦を更新する。</summary>
    Task ProcessAsync(string batchIdentifier, CancellationToken cancellationToken = default);
}

/// <summary>保存済み下書きの検証結果保存と推薦再計算を実行する。</summary>
public sealed class DraftPostProcessingService(
    TaskRepository taskRepository,
    TaskValidationService taskValidationService,
    RecommendationService recommendationService) : IDraftPostProcessingService
{
    // 保存層、検証、推薦サービスを保持する。
    private readonly TaskRepository repository = taskRepository;
    private readonly TaskValidationService validation = taskValidationService;
    private readonly RecommendationService recommendations = recommendationService;

    /// <summary>保存済み下書きを全体グラフで検証して推薦を更新する。</summary>
    public async Task ProcessAsync(
        string batchIdentifier,
        CancellationToken cancellationToken = default)
    {
        // 対象バッチの検証結果を保存した後で最新推薦を再計算する。
        List<ManagedTask> tasks = await repository.GetTasksAsync(cancellationToken);
        validation.ValidateAll(tasks);
        foreach (ManagedTask draftTask in tasks.Where(task =>
            task.DraftBatchIdentifier == batchIdentifier))
        {
            await repository.SaveTaskAsync(
                draftTask,
                "下書き検証",
                TaskConstants.SystemSource,
                cancellationToken);
        }
        await recommendations.RefreshAsync(cancellationToken);
    }
}

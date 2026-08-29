using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>タスク単体と依存グラフの整合性を検証する。</summary>
public sealed class TaskValidationService
{
    /// <summary>全タスクを検証して各タスクのエラー文を更新する。</summary>
    public void ValidateAll(IReadOnlyList<ManagedTask> tasks)
    {
        // タスクID参照と循環をまとめて確認する。
        Dictionary<string, ManagedTask> taskMap = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Identifier))
            .GroupBy(task => task.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (ManagedTask task in tasks)
        {
            task.ValidationResult = ValidateTask(task, taskMap);
        }
        List<string> cyclePath = FindCycle(tasks);
        if (cyclePath.Count > 0)
        {
            string cycleMessage = $"依存関係が循環しています: {string.Join(" → ", cyclePath)}";
            foreach (string taskIdentifier in cyclePath.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (taskMap.TryGetValue(taskIdentifier, out ManagedTask? task))
                {
                    task.ValidationResult = AppendMessage(task.ValidationResult, cycleMessage);
                }
            }
        }
    }

    /// <summary>1件の必須値と参照先を検証する。</summary>
    private static string ValidateTask(ManagedTask task, IReadOnlyDictionary<string, ManagedTask> taskMap)
    {
        // 必須値、数値範囲、親子期限、依存先を順に確認する。
        string validationMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(task.Identifier))
        {
            validationMessage = AppendMessage(validationMessage, "タスクIDがありません");
        }
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            validationMessage = AppendMessage(validationMessage, "名称がありません");
        }
        if (task.EstimatedMinutes <= 0)
        {
            validationMessage = AppendMessage(validationMessage, "見積時間がありません");
        }
        if (task.Importance is < 1 or > 5)
        {
            validationMessage = AppendMessage(validationMessage, "重要度は1から5で指定してください");
        }
        if (!string.IsNullOrWhiteSpace(task.ParentIdentifier))
        {
            if (!taskMap.TryGetValue(task.ParentIdentifier, out ManagedTask? parentTask))
            {
                validationMessage = AppendMessage(validationMessage, $"親タスクが見つかりません: {task.ParentIdentifier}");
            }
            else if (task.DeadlineAt.HasValue && parentTask.DeadlineAt.HasValue && task.DeadlineAt > parentTask.DeadlineAt)
            {
                validationMessage = AppendMessage(validationMessage, "子タスクの期限が親タスクの期限を超えています");
            }
        }
        foreach (string dependencyIdentifier in task.DependencyIdentifiers)
        {
            if (string.Equals(dependencyIdentifier, task.Identifier, StringComparison.OrdinalIgnoreCase))
            {
                validationMessage = AppendMessage(validationMessage, "自分自身へ依存しています");
            }
            else if (!taskMap.TryGetValue(dependencyIdentifier, out ManagedTask? dependencyTask))
            {
                validationMessage = AppendMessage(validationMessage, $"依存先が見つかりません: {dependencyIdentifier}");
            }
            else if (dependencyTask.Status == TaskConstants.CancelledStatus)
            {
                validationMessage = AppendMessage(validationMessage, $"依存先が中止されています: {dependencyIdentifier}");
            }
        }
        return validationMessage;
    }

    /// <summary>依存グラフから最初の循環経路を検出する。</summary>
    public static List<string> FindCycle(IReadOnlyList<ManagedTask> tasks)
    {
        // 深さ優先探索で探索中のノードへ戻る経路を探す。
        Dictionary<string, ManagedTask> taskMap = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.Identifier))
            .GroupBy(task => task.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> visitStates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string taskIdentifier in taskMap.Keys)
        {
            List<string> cyclePath = VisitNode(taskIdentifier, taskMap, visitStates, []);
            if (cyclePath.Count > 0)
            {
                return cyclePath;
            }
        }
        return [];
    }

    /// <summary>依存グラフの1ノードを再帰的に探索する。</summary>
    private static List<string> VisitNode(
        string taskIdentifier,
        IReadOnlyDictionary<string, ManagedTask> taskMap,
        IDictionary<string, string> visitStates,
        List<string> traversalPath)
    {
        // 未訪問、探索中、完了の3状態で循環を判定する。
        visitStates.TryGetValue(taskIdentifier, out string? currentVisitState);
        if (currentVisitState == "完了")
        {
            return [];
        }
        if (currentVisitState == "探索中")
        {
            int cycleStartIndex = traversalPath.FindIndex(identifier => string.Equals(identifier, taskIdentifier, StringComparison.OrdinalIgnoreCase));
            return traversalPath.Skip(cycleStartIndex).Append(taskIdentifier).ToList();
        }
        visitStates[taskIdentifier] = "探索中";
        traversalPath.Add(taskIdentifier);
        foreach (string dependencyIdentifier in taskMap[taskIdentifier].DependencyIdentifiers)
        {
            if (!taskMap.ContainsKey(dependencyIdentifier))
            {
                continue;
            }
            List<string> cyclePath = VisitNode(dependencyIdentifier, taskMap, visitStates, traversalPath);
            if (cyclePath.Count > 0)
            {
                return cyclePath;
            }
        }
        traversalPath.RemoveAt(traversalPath.Count - 1);
        visitStates[taskIdentifier] = "完了";
        return [];
    }

    /// <summary>検証文へ重複しないメッセージを追加する。</summary>
    private static string AppendMessage(string currentMessage, string additionalMessage)
    {
        // セミコロン区切りで画面表示しやすく連結する。
        if (currentMessage.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(additionalMessage, StringComparer.Ordinal))
        {
            return currentMessage;
        }
        return string.IsNullOrWhiteSpace(currentMessage) ? additionalMessage : $"{currentMessage}; {additionalMessage}";
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManager.Domain;

namespace TaskManager.Services;

/// <summary>省略と明示的な解除を区別してタスクの更新入力を検証する。</summary>
public static class TaskUpdate
{
    // タスクのJSON変換設定と、更新可能なモデル項目の対応を保持する。
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
    private static readonly Dictionary<string, PropertyInfo> Properties = typeof(ManagedTask).GetProperties()
        .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>現在値の複製へ指定項目だけを反映し、不正入力は保存前に拒否する。</summary>
    public static ManagedTask Merge(ManagedTask existingTask, JsonElement changes)
    {
        // 空入力・配列・重複項目・未知の項目を拒否して入力ミスを検出する。
        if (changes.ValueKind != JsonValueKind.Object || !changes.EnumerateObject().Any())
        {
            throw new InvalidOperationException("更新には変更項目を含むJSONオブジェクトを指定してください。");
        }
        ManagedTask replacementTask = JsonSerializer.Deserialize<ManagedTask>(JsonSerializer.Serialize(existingTask, Options), Options)!;
        HashSet<string> suppliedNames = new(StringComparer.OrdinalIgnoreCase);
        NullabilityInfoContext nullability = new();
        foreach (JsonProperty change in changes.EnumerateObject())
        {
            if (!Properties.TryGetValue(change.Name, out PropertyInfo? property) || !suppliedNames.Add(change.Name))
            {
                throw new InvalidOperationException($"不明または重複した更新項目です: {change.Name}");
            }
            if (change.Value.ValueKind == JsonValueKind.Null
                && nullability.Create(property).WriteState != NullabilityState.Nullable)
            {
                throw new InvalidOperationException($"nullにできない更新項目です: {change.Name}");
            }
            try
            {
                // 期限はモデルの専用変換を通し、日付だけの部分更新にも20時を補う。
                object? replacementValue = property.Name == nameof(ManagedTask.DeadlineAt)
                    ? JsonSerializer.Deserialize<ManagedTask>("{\"deadlineAt\":" + change.Value.GetRawText() + "}", Options)!.DeadlineAt
                    : change.Value.Deserialize(property.PropertyType, Options);
                property.SetValue(replacementTask, replacementValue);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"更新項目の値が不正です: {change.Name}", exception);
            }
        }

        // 期限の明示変更はプロジェクト既定に戻さず、名称の消失も防ぐ。
        if (suppliedNames.Contains("deadlineAt") && !suppliedNames.Contains("deadlineOrigin"))
        {
            replacementTask.DeadlineOrigin = replacementTask.DeadlineAt.HasValue ? "explicit" : "none";
        }
        if (string.IsNullOrWhiteSpace(replacementTask.Title))
        {
            throw new InvalidOperationException("タスク名称を空にすることはできません。");
        }
        return replacementTask;
    }
}

using System.Text.Json;

namespace TaskManager.Cli;

/// <summary>読取JSONの形を保ちながら指定項目だけを表示する。</summary>
public static class CliJsonOutput
{
    public static string[]? ParseFields(IReadOnlyList<string> arguments)
    {
        // オプションの欠落・重複と不正な項目指定をAPI実行前に拒否する。
        string[]? fields = null;
        for (int position = 0; position < arguments.Count; position++)
        {
            if (!string.Equals(arguments[position], "--fields", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields is not null || position + 1 >= arguments.Count)
                throw new InvalidOperationException("--fieldsはカンマ区切りの項目名を1回だけ指定してください。");
            fields = arguments[position + 1].Split(',', StringSplitOptions.TrimEntries);
            if (fields.Any(field => field.Split('.').Any(part => part.Length == 0 || !part.All(character => char.IsLetterOrDigit(character) || character == '_'))))
                throw new InvalidOperationException("--fieldsの項目名が不正です。");
            fields = fields.Distinct(StringComparer.Ordinal).ToArray();
        }
        return fields;
    }

    public static JsonElement SelectFields(object? response, string[] fields)
    {
        // 配列・オブジェクト・nullを維持し、JSON文字列内の日本語もそのまま扱う。
        JsonElement element = JsonSerializer.SerializeToElement(response);
        return JsonSerializer.SerializeToElement(SelectElement(element, fields));
    }

    private static object? SelectElement(JsonElement element, string[] fields)
    {
        // 配列は各要素へ同じ選択を適用し、ネストしたオブジェクトは必要な枝だけを残す。
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(item => SelectElement(item, fields)).ToArray();
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("--fieldsの対象はオブジェクトまたはその配列にしてください。");
        Dictionary<string, object?> selected = [];
        foreach (IGrouping<string, string> group in fields.GroupBy(field => field.Split('.')[0], StringComparer.Ordinal))
        {
            if (!element.TryGetProperty(group.Key, out JsonElement value))
                throw new InvalidOperationException($"JSONに項目がありません: {group.Key}");
            selected[group.Key] = group.Contains(group.Key)
                ? value
                : SelectElement(value, group.Select(field => field[(group.Key.Length + 1)..]).ToArray());
        }
        return selected;
    }
}

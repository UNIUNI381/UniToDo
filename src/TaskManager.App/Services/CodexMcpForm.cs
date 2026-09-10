using System.Text.Json;

namespace TaskManager.Services;

/// <summary>MCPの単純な確認・入力フォームだけをWeb用に検証する。</summary>
public static class CodexMcpForm
{
    public static bool IsSupported(JsonElement parameters)
    {
        // URL認証や拡張フォームを承認フォームと混同せず、解釈できる標準形式だけを受け付ける。
        if (!parameters.TryGetProperty("mode", out JsonElement mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "form"
            || !parameters.TryGetProperty("requestedSchema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object"
            || !schema.TryGetProperty("properties", out JsonElement properties) || properties.ValueKind != JsonValueKind.Object
            || properties.EnumerateObject().Count() > 20) return false;
        foreach (JsonProperty property in schema.EnumerateObject())
            if (property.Name is not ("type" or "properties" or "required" or "$schema")) return false;
        if (schema.TryGetProperty("required", out JsonElement required) && required.ValueKind != JsonValueKind.Null)
        {
            if (required.ValueKind != JsonValueKind.Array) return false;
            foreach (JsonElement field in required.EnumerateArray())
                if (field.ValueKind != JsonValueKind.String || !properties.TryGetProperty(field.GetString()!, out _)) return false;
        }
        foreach (JsonProperty field in properties.EnumerateObject())
        {
            JsonElement definition = field.Value;
            if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("type", out JsonElement fieldType)
                || fieldType.ValueKind != JsonValueKind.String || fieldType.GetString() is not ("string" or "boolean")) return false;
            foreach (JsonProperty constraint in definition.EnumerateObject())
                if (constraint.Name is not ("type" or "title" or "description" or "enum" or "enumNames" or "default")) return false;
            foreach (string label in new[] { "title", "description" })
                if (definition.TryGetProperty(label, out JsonElement value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
            if (definition.TryGetProperty("enum", out JsonElement choices))
            {
                if (fieldType.GetString() != "string" || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() is < 1 or > 50
                    || choices.EnumerateArray().Any(choice => choice.ValueKind != JsonValueKind.String)) return false;
            }
        }
        return true;
    }

    public static object BuildContent(JsonElement parameters, Dictionary<string, string[]>? answers)
    {
        // ブラウザの回答を保存済みスキーマで再検証し、未入力や未知の値を勝手に補わない。
        if (!IsSupported(parameters)) throw new ArgumentException("この確認形式はWeb画面では承認できません。");
        JsonElement schema = parameters.GetProperty("requestedSchema");
        JsonElement properties = schema.GetProperty("properties");
        HashSet<string> required = schema.TryGetProperty("required", out JsonElement requiredFields) && requiredFields.ValueKind == JsonValueKind.Array
            ? requiredFields.EnumerateArray().Select(field => field.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        Dictionary<string, object> content = new(StringComparer.Ordinal);
        if (answers is not null && answers.Keys.Any(key => !properties.TryGetProperty(key, out _)))
            throw new ArgumentException("要求されていない回答項目が含まれています。");
        foreach (JsonProperty field in properties.EnumerateObject())
        {
            if (answers is null || !answers.TryGetValue(field.Name, out string[]? values) || values.Length == 0)
            {
                if (required.Contains(field.Name)) throw new ArgumentException("必要な項目へ回答してください。");
                continue;
            }
            if (values.Length != 1 || values[0] is null || values[0].Length > 4000) throw new ArgumentException("回答は各4,000文字以内にしてください。");
            string value = values[0];
            if (field.Value.GetProperty("type").GetString() == "boolean")
            {
                if (value is not ("true" or "false")) throw new ArgumentException("確認項目の回答が不正です。");
                content[field.Name] = value == "true";
            }
            else
            {
                if (field.Value.TryGetProperty("enum", out JsonElement choices) && !choices.EnumerateArray().Any(choice => choice.GetString() == value))
                    throw new ArgumentException("提示された選択肢から回答してください。");
                content[field.Name] = value;
            }
        }
        return content;
    }
}

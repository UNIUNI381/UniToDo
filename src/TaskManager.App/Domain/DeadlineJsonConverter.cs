using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskManager.Domain;

/// <summary>日付だけの期限をローカル20時として読み込む。</summary>
public sealed class DeadlineJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 日付だけの場合に限り既定時刻を補い、明示された0時も保持する。
        if (reader.TokenType == JsonTokenType.String
            && DateOnly.TryParseExact(reader.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly deadlineDate))
        {
            DateTime localDeadline = deadlineDate.ToDateTime(new TimeOnly(20, 0));
            return new DateTimeOffset(localDeadline, TimeZoneInfo.Local.GetUtcOffset(localDeadline));
        }
        return reader.GetDateTimeOffset();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        // 保存済み期限をオフセット付き日時としてそのまま出力する。
        writer.WriteStringValue(value);
    }
}

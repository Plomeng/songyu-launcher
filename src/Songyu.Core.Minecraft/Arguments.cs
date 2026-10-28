using System.Text.Json;
using System.Text.Json.Serialization;

namespace Songyu.Core.Minecraft;

/// <summary>
/// arguments 数组里的一个元素。原始 JSON 有两种形态：
/// <code>"--demo"</code> 或 <code>{ "rules": [...], "value": "--demo" }</code>（value 也可能是数组）。
/// 这里统一成 { Rules, Values }，求值时再决定是否采用。
/// </summary>
public sealed class ArgumentElement
{
    public List<Rule>? Rules { get; init; }

    public List<string> Values { get; init; } = [];

    public bool IsAllowed(RuleContext context) => RuleEvaluator.IsAllowed(Rules, context);
}

public sealed class ArgumentsJson
{
    [JsonPropertyName("game")]
    public List<ArgumentElement> Game { get; init; } = [];

    [JsonPropertyName("jvm")]
    public List<ArgumentElement> Jvm { get; init; } = [];
}

/// <summary>
/// 把 "字符串" 与 "{rules, value}" 两种形态读成同一个模型。
/// </summary>
public sealed class ArgumentElementConverter : JsonConverter<ArgumentElement>
{
    public override ArgumentElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ArgumentElement { Values = [reader.GetString() ?? string.Empty] };

            case JsonTokenType.StartObject:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;

                List<Rule>? rules = null;
                if (root.TryGetProperty("rules", out var rulesElement))
                {
                    rules = JsonSerializer.Deserialize<List<Rule>>(rulesElement.GetRawText(), options);
                }

                var values = new List<string>();
                if (root.TryGetProperty("value", out var valueElement))
                {
                    Collect(valueElement, values);
                }

                return new ArgumentElement { Rules = rules, Values = values };
            }

            default:
                throw new JsonException($"无法解析的参数元素类型：{reader.TokenType}");
        }
    }

    private static void Collect(JsonElement element, List<string> target)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                target.Add(element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, target);
                }
                break;
        }
    }

    public override void Write(Utf8JsonWriter writer, ArgumentElement value, JsonSerializerOptions options)
    {
        // 只读不写：启动器不产出这种格式，留空实现避免误用。
        throw new NotSupportedException("ArgumentElement 不需要序列化。");
    }
}

public static class SongyuJson
{
    /// <summary>全局统一的 JSON 选项：宽松、忽略大小写、允许注释与尾逗号。</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new ArgumentElementConverter());
        return options;
    }
}

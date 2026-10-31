using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBrook.Privacy;

/// <summary>
/// 工具实参 JSON 递归变换（15 号设计 §7.1 / §7.4）：字符串值**与对象属性名（key）**统一过变换函数，
/// 出站（Mask）与入站（Restore）共用，方向由变换函数决定。
/// 支持 IDictionary{string,object?} / object[] / List / JsonElement / JsonNode / 标量；其余类型透传 + 由调用方记日志。
/// </summary>
internal static class PrivacyJson
{
    public static (object? Value, bool Changed) Transform(object? value, Func<string, string> transform)
    {
        switch (value)
        {
            case null:
                return (null, false);
            case string s:
            {
                var t = transform(s);
                return (t, !string.Equals(t, s, StringComparison.Ordinal));
            }
            case JsonElement element:
            {
                var (node, changed) = TransformNode(JsonNode.Parse(element.GetRawText()), transform);
                return (changed ? JsonSerializer.SerializeToElement(node) : value, changed);
            }
            case JsonNode node:
                return TransformNode(node.DeepClone(), transform);
            case IDictionary<string, object?> dict:
            {
                var result = new Dictionary<string, object?>(dict.Count);
                var changed = false;
                foreach (var (key, item) in dict)
                {
                    var (newKey, keyChanged) = Transform(key, transform);
                    var (newValue, valueChanged) = Transform(item, transform);
                    changed |= keyChanged || valueChanged || !string.Equals((string)newKey!, key, StringComparison.Ordinal);
                    result[(string)newKey!] = newValue;
                }
                return (result, changed);
            }
            case object[] array:
            {
                var result = new object?[array.Length];
                var changed = false;
                for (var i = 0; i < array.Length; i++)
                {
                    var (item, itemChanged) = Transform(array[i], transform);
                    result[i] = item;
                    changed |= itemChanged;
                }
                return (result, changed);
            }
            case List<object?> list:
            {
                var result = new List<object?>(list.Count);
                var changed = false;
                foreach (var item in list)
                {
                    var (v, itemChanged) = Transform(item, transform);
                    result.Add(v);
                    changed |= itemChanged;
                }
                return (result, changed);
            }
            default:
                return (value, false);   // 数字 / 布尔等标量原样
        }
    }

    private static (JsonNode?, bool) TransformNode(JsonNode? node, Func<string, string> transform)
    {
        switch (node)
        {
            case null:
                return (null, false);
            case JsonObject obj:
            {
                var result = new JsonObject();
                var changed = false;
                foreach (var (key, value) in obj)
                {
                    var (newKey, keyChanged) = Transform(key, transform);
                    var (newValue, valueChanged) = TransformNode(value, transform);
                    changed |= keyChanged || valueChanged || !string.Equals((string)newKey!, key, StringComparison.Ordinal);
                    result[(string)newKey!] = newValue;
                }
                return (result, changed);
            }
            case JsonArray array:
            {
                var result = new JsonArray();
                var changed = false;
                foreach (var item in array)
                {
                    var (v, itemChanged) = TransformNode(item, transform);
                    changed |= itemChanged;
                    result.Add(v);
                }
                return (result, changed);
            }
            case JsonValue value:
            {
                if (value.TryGetValue<string>(out var s))
                {
                    var t = transform(s);
                    return (JsonValue.Create(t), !string.Equals(t, s, StringComparison.Ordinal));
                }
                return (value.DeepClone(), false);   // 数字 / 布尔
            }
            default:
                return (node.DeepClone(), false);
        }
    }
}

using System.Text.Json;

namespace ServiceCodeChanger.Sbis;

internal static class JsonElementExtensions
{
    /// <summary>Строковое (или числовое) значение поля либо null.</summary>
    public static string? Str(this JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Вложенный объект либо default (ValueKind.Undefined).</summary>
    public static JsonElement Obj(this JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Object
            ? v
            : default;

    /// <summary>
    /// Элементы поля-массива. СБИС иногда отдаёт одиночный объект вместо массива из одного элемента —
    /// такой случай тоже поддерживается.
    /// </summary>
    public static IEnumerable<JsonElement> Items(this JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            yield break;

        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
                yield return item;
        }
        else if (v.ValueKind == JsonValueKind.Object)
        {
            yield return v;
        }
    }
}

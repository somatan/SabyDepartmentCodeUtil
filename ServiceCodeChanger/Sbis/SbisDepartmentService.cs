using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServiceCodeChanger.Sbis;

/// <summary>Подразделение из sabyDepartment.Read. <see cref="Raw"/> — объект как его вернул СБИС.</summary>
public sealed record SbisDepartment(string Id, string? Title, string? Code, JsonElement Raw);

/// <summary>Чтение и изменение подразделений (API «Управление персоналом», sabyDepartment.*).</summary>
public sealed class SbisDepartmentService
{
    private const string ServiceEndpoint = "/service/?srv=1";
    private const int ReadBatchSize = 500; // ограничение sabyDepartment.Read

    /// <summary>
    /// Поля, которые sabyDepartment.Update требует и при обновлении (вопреки документации,
    /// где они названы обязательными только при создании). Передаём их как вернул Read.
    /// </summary>
    private static readonly string[] UpdateKeyFields = ["id", "title", "type", "structureType", "parent", "organization"];

    private readonly SbisRpcClient _rpc;

    public SbisDepartmentService(SbisRpcClient rpc) => _rpc = rpc;

    /// <summary>
    /// Читает подразделения по UUID. Отсутствующие в СБИС в результат не попадают.
    /// Метод ограничен 60 вызовами в минуту, поэтому читаем одной пачкой, а не по одному.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SbisDepartment>> ReadAsync(
        IEnumerable<string> ids, CancellationToken ct = default)
    {
        var found = new Dictionary<string, SbisDepartment>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ids.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(ReadBatchSize))
        {
            var result = await _rpc.CallAsync(
                ServiceEndpoint, "sabyDepartment.Read", new { departments = batch }, ct);

            var items = result.ValueKind == JsonValueKind.Array ? result.EnumerateArray() : result.Items("result");
            foreach (var item in items)
            {
                var id = item.Str("id");
                if (!string.IsNullOrEmpty(id))
                    found[id] = new SbisDepartment(id, item.Str("title"), item.Str("code"), item.Clone());
            }
        }
        return found;
    }

    /// <summary>
    /// Меняет код подразделения. Подразделение берётся из <see cref="ReadAsync"/> — это гарантирует,
    /// что UUID существует (иначе Update искал бы по названию и коду и мог бы создать новое).
    /// Ключевые поля передаются без изменений, меняется только code.
    /// </summary>
    public async Task UpdateCodeAsync(SbisDepartment department, string newCode, CancellationToken ct = default)
    {
        var payload = new JsonObject();
        foreach (var name in UpdateKeyFields)
        {
            if (department.Raw.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                payload[name] = JsonNode.Parse(value.GetRawText());
        }
        payload["id"] = department.Id;
        payload["code"] = newCode;

        // Update принимает один параметр — массив «departments» (в запросе одно подразделение).
        await _rpc.CallAsync(
            ServiceEndpoint,
            "sabyDepartment.Update",
            new { departments = new[] { payload } },
            ct);
    }
}

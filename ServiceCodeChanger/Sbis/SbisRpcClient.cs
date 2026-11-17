using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ServiceCodeChanger.Sbis;

/// <summary>
/// Минимальный JSON-RPC 2.0 клиент для API СБИС.
/// Если задан <see cref="SessionId"/>, он передаётся в заголовке X-SBISSessionID.
/// </summary>
public sealed class SbisRpcClient : IDisposable
{
    private static readonly MediaTypeHeaderValue JsonRpcMediaType =
        new("application/json-rpc") { CharSet = "utf-8" };

    // Имена полей СБИС чувствительны к регистру («Параметр», «Логин»), поэтому без camelCase.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private int _requestId;

    public SbisRpcClient(Uri baseAddress, TimeSpan? timeout = null)
    {
        _http = new HttpClient
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
        };
    }

    public string? SessionId { get; set; }

    /// <summary>Если задан — сюда пишутся запросы и ответы (кроме помеченных как sensitive).</summary>
    public TextWriter? DebugLog { get; set; }

    /// <summary>Вызывает метод API и возвращает поле result ответа.</summary>
    /// <param name="sensitive">Не писать тело запроса и ответа в отладочный лог (пароли, сессии).</param>
    public async Task<JsonElement> CallAsync(
        string endpoint, string method, object parameters, CancellationToken ct = default, bool sensitive = false)
    {
        var body = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
            id = Interlocked.Increment(ref _requestId),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, JsonRpcMediaType, JsonOptions),
        };
        if (!string.IsNullOrEmpty(SessionId))
            request.Headers.Add("X-SBISSessionID", SessionId);

        if (DebugLog is not null)
        {
            var payload = sensitive ? "(скрыто)" : JsonSerializer.Serialize(parameters, JsonOptions);
            DebugLog.WriteLine($"→ {method} {payload}");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SbisException($"Не удалось подключиться к {_http.BaseAddress}: {ex.Message}", inner: ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SbisException($"Превышено время ожидания ответа от {_http.BaseAddress}", inner: ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            DebugLog?.WriteLine($"← HTTP {(int)response.StatusCode} {(sensitive ? "(скрыто)" : text)}");

            // СБИС часто отдаёт ошибку с HTTP 4xx/5xx, но с JSON-RPC телом — поэтому сначала разбираем тело.
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw new SbisException(
                    $"Неожиданный ответ сервера (HTTP {(int)response.StatusCode} {response.ReasonPhrase})",
                    Truncate(text, 500));
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    throw ToException(error);

                if (!response.IsSuccessStatusCode)
                    throw new SbisException(
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", Truncate(text, 500));

                return root.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
        }
    }

    private static SbisException ToException(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return new SbisException(error.GetString() ?? "Неизвестная ошибка");

        string? message = GetString(error, "message");
        string? details = GetString(error, "details");
        int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : null;
        return new SbisException(message ?? details ?? "Неизвестная ошибка СБИС", details, code);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}

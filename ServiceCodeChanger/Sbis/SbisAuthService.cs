using System.Text.Json;

namespace ServiceCodeChanger.Sbis;

/// <summary>Вход и выход в СБИС по логину и паролю.</summary>
public sealed class SbisAuthService
{
    private const string AuthEndpoint = "/auth/service/";

    private readonly SbisRpcClient _rpc;

    public SbisAuthService(SbisRpcClient rpc) => _rpc = rpc;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_rpc.SessionId);

    /// <summary>
    /// Аутентифицирует пользователя и сохраняет идентификатор сессии в RPC-клиенте,
    /// чтобы последующие вызовы API шли уже от имени пользователя.
    /// </summary>
    public async Task<string> LoginAsync(string login, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var result = await _rpc.CallAsync(
            AuthEndpoint,
            "СБИС.Аутентифицировать",
            new { Параметр = new { Логин = login, Пароль = password } },
            ct,
            sensitive: true);

        var sessionId = result.ValueKind == JsonValueKind.String ? result.GetString() : null;
        if (string.IsNullOrEmpty(sessionId))
            throw new SbisException("Сервер не вернул идентификатор сессии", result.ToString());

        _rpc.SessionId = sessionId;
        return sessionId;
    }

    /// <summary>Завершает текущую сессию. Ошибки выхода не критичны и проглатываются.</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return;

        try
        {
            await _rpc.CallAsync(AuthEndpoint, "СБИС.Выход", new { }, ct, sensitive: true);
        }
        catch (SbisException)
        {
            // Сессия всё равно истечёт сама.
        }
        finally
        {
            _rpc.SessionId = null;
        }
    }
}

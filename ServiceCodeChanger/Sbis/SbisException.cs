namespace ServiceCodeChanger.Sbis;

/// <summary>Ошибка, которую вернул сервис СБИС (JSON-RPC error) или транспорт.</summary>
public sealed class SbisException : Exception
{
    public int? Code { get; }
    public string? Details { get; }

    public SbisException(string message, string? details = null, int? code = null, Exception? inner = null)
        : base(message, inner)
    {
        Details = details;
        Code = code;
    }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Details) || Details == Message
            ? Message
            : $"{Message} ({Details})";
}

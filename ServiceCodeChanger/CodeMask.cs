namespace ServiceCodeChanger;

/// <summary>
/// Маска кода: группа символов «#» заменяется номером с ведущими нулями.
/// «БТ###» → БТ001, БТ002, … БТ999, БТ1000. Группа «#» в маске ровно одна.
/// </summary>
public sealed class CodeMask
{
    private readonly string _prefix;
    private readonly string _suffix;
    private readonly int _width;

    private CodeMask(string prefix, string suffix, int width)
    {
        _prefix = prefix;
        _suffix = suffix;
        _width = width;
    }

    public static bool TryParse(string? mask, out CodeMask? result, out string error)
    {
        result = null;
        mask = mask?.Trim();
        if (string.IsNullOrEmpty(mask))
        {
            error = "Маска пустая.";
            return false;
        }

        var start = mask.IndexOf('#');
        if (start < 0)
        {
            error = "В маске нет «#» — места для номера.";
            return false;
        }

        var end = start;
        while (end < mask.Length && mask[end] == '#')
            end++;
        if (mask.IndexOf('#', end) >= 0)
        {
            error = "В маске должна быть одна группа «#».";
            return false;
        }

        result = new CodeMask(mask[..start], mask[end..], end - start);
        error = "";
        return true;
    }

    public string Format(int number) => _prefix + number.ToString().PadLeft(_width, '0') + _suffix;

    /// <summary>Номер из кода, построенного по этой маске, или null.</summary>
    public int? NumberOf(string code)
    {
        if (code.Length <= _prefix.Length + _suffix.Length
            || !code.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
            || !code.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var middle = code[_prefix.Length..^_suffix.Length];
        return middle.All(char.IsAsciiDigit) && int.TryParse(middle, out var n) ? n : null;
    }

    /// <summary>
    /// Выдаёт <paramref name="count"/> кодов, которых нет в <paramref name="taken"/>.
    /// Нумерация продолжается после наибольшего уже занятого номера по этой маске,
    /// выданные коды добавляются в <paramref name="taken"/>.
    /// </summary>
    public List<string> Generate(int count, ISet<string> taken)
    {
        var next = taken.Select(NumberOf).Max() is { } max ? max + 1 : 1;
        var codes = new List<string>(count);
        while (codes.Count < count)
        {
            var code = Format(next++);
            if (taken.Add(code))
                codes.Add(code);
        }
        return codes;
    }

    public override string ToString() => _prefix + new string('#', _width) + _suffix;
}

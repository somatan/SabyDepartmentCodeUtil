using System.Text;
using ServiceCodeChanger;
using ServiceCodeChanger.Sbis;

Console.OutputEncoding = Encoding.UTF8;
// InputEncoding намеренно не трогаем: на Windows с UTF-8 Console.ReadLine теряет кириллицу.

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Адрес и учётные данные можно задать через переменные окружения, иначе спросим в консоли.
var baseUrl = Environment.GetEnvironmentVariable("SBIS_URL") ?? "https://online.sbis.ru";
var login = Environment.GetEnvironmentVariable("SBIS_LOGIN") ?? Prompt("Логин: ");
// Головная организация, подразделения которой выводим. КПП нужен, только если по ИНН несколько юрлиц.
var orgInn = Environment.GetEnvironmentVariable("SBIS_ORG_INN")?.Trim();
var orgKpp = Environment.GetEnvironmentVariable("SBIS_ORG_KPP")?.Trim();
if (string.IsNullOrEmpty(orgInn))
{
    Console.Error.WriteLine("Не задан ИНН головной организации: переменная SBIS_ORG_INN в launchSettings.json.");
    return 1;
}

// Показывать только подразделения без кода (их и нужно заполнять).
var onlyWithoutCode = Environment.GetEnvironmentVariable("SBIS_ONLY_WITHOUT_CODE") is "1" or "true";
// Маска для массового заполнения кодов («БТ###»); если не задана — спросим при заполнении.
var codeMask = Environment.GetEnvironmentVariable("SBIS_CODE_MASK");

var password = Environment.GetEnvironmentVariable("SBIS_PASSWORD") ?? PromptPassword("Пароль: ");

if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("Логин и пароль не должны быть пустыми.");
    return 1;
}

using var rpc = new SbisRpcClient(new Uri(baseUrl));
if (Environment.GetEnvironmentVariable("SBIS_DEBUG") is "1" or "true")
    rpc.DebugLog = Console.Error;

var auth = new SbisAuthService(rpc);
var directory = new SbisDirectoryService(rpc);
var departments = new SbisDepartmentService(rpc);

try
{
    Console.WriteLine($"Авторизация в {baseUrl}…");
    var sessionId = await auth.LoginAsync(login, password, cts.Token);
    Console.WriteLine($"Успешно. Сессия: {Mask(sessionId)}");

    Console.WriteLine();
    Console.WriteLine("Загрузка списка организаций…");
    var organizations = await directory.ListOurOrganizationsAsync(cts.Token);
    if (organizations.Count == 0)
    {
        Console.Error.WriteLine("В аккаунте не найдено ни одной организации.");
        return 1;
    }

    var organization = FindHeadOrganization(organizations, orgInn, orgKpp);
    if (organization is null)
    {
        Console.Error.WriteLine(
            $"Головная организация с ИНН {orgInn}{(string.IsNullOrEmpty(orgKpp) ? "" : $" и КПП {orgKpp}")} не найдена в аккаунте.");
        return 1;
    }
    Console.WriteLine($"Организация: {organization}");

    while (true)
    {
        Console.WriteLine();
        Console.WriteLine($"Загрузка подразделений: {organization.Name}…");
        var subdivisions = await directory.ListSubdivisionsAsync(organization, cts.Token);

        // Корень структуры — сама головная организация: его не показываем и не меняем,
        // иначе заполнение по маске выдало бы код самой организации.
        var editable = subdivisions.Where(s => !s.IsRoot).ToList();

        // В таблице и выборе — только отфильтрованные; проверка занятости кода — по всем подразделениям.
        var shown = onlyWithoutCode
            ? editable.Where(s => string.IsNullOrWhiteSpace(s.Code)).ToList()
            : editable;
        if (onlyWithoutCode)
            Console.WriteLine($"Показаны только подразделения без кода: {shown.Count} из {editable.Count}.");

        PrintSubdivisions(shown);
        if (shown.Count == 0)
            break;

        // Занятые коды: все подразделения организации и коды её филиалов (код обособленного
        // подразделения синхронизируется с кодом филиала). Регистр не различаем.
        var takenCodes = subdivisions.Select(s => s.Code)
            .Concat(organizations.Where(o => o.Inn == organization.Inn).Select(o => o.BranchCode))
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = ChooseMany(shown, out var fillByMask);
        if (selected.Count == 0 && !fillByMask)
            break;

        var changes = fillByMask
            ? BuildMaskChanges(editable, takenCodes, codeMask)
            : AskNewCodes(selected, takenCodes);
        if (changes.Count == 0)
        {
            Console.WriteLine("Нечего менять.");
            continue;
        }

        Console.WriteLine();
        Console.WriteLine("Будут изменены коды подразделений:");
        foreach (var (sub, newCode) in changes)
            Console.WriteLine($"  {sub.Name}: «{sub.Code ?? ""}» → «{newCode}»");

        if (!Confirm("Применить? (да/нет): "))
        {
            Console.WriteLine("Отменено, ничего не изменено.");
            continue;
        }

        await ApplyAsync(changes, departments, cts.Token);
    }
}
catch (SbisException ex)
{
    Console.Error.WriteLine($"Ошибка СБИС: {ex}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Отменено.");
    return 2;
}
finally
{
    await auth.LogoutAsync(CancellationToken.None);
}

return 0;

/// Головная организация по ИНН — так же, как при построении дерева (см. OrganizationTree).
/// Если задан КПП, головная запись должна иметь именно его.
static SbisOrganization? FindHeadOrganization(
    IReadOnlyList<SbisOrganization> organizations, string inn, string? kpp)
{
    var head = OrganizationTree.Build(organizations).FirstOrDefault(n => n.Head.Inn == inn)?.Head;
    if (head is null)
        return null;
    if (!string.IsNullOrEmpty(kpp) && head.Kpp != kpp)
        return null;
    return head;
}

static void PrintSubdivisions(IReadOnlyList<SbisSubdivision> subdivisions)
{
    if (subdivisions.Count == 0)
    {
        Console.WriteLine("Подразделений не найдено.");
        return;
    }

    var rows = subdivisions
        .Select((s, i) => new[]
        {
            (i + 1).ToString(),
            s.Code ?? "",
            s.Name,
            s.SectionName ?? "",
            s.Id,
        })
        .ToList();
    string[] header = ["№", "Код", "Название", "Раздел", "Идентификатор"];

    var widths = header
        .Select((h, col) => Math.Min(50, rows.Select(r => r[col].Length).Append(h.Length).Max()))
        .ToArray();

    string Line(string[] cells) => string.Join("  ", cells.Select((c, col) =>
        (c.Length > widths[col] ? c[..(widths[col] - 1)] + "…" : c).PadRight(widths[col])));

    Console.WriteLine($"Подразделений: {subdivisions.Count}");
    Console.WriteLine(Line(header));
    Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
    for (var i = 0; i < rows.Count; i++)
    {
        // Подразделения без кода — красным, их нужно заполнить.
        var noCode = string.IsNullOrWhiteSpace(subdivisions[i].Code);
        if (noCode)
            Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(Line(rows[i]));
        if (noCode)
            Console.ResetColor();
    }

    var withoutCode = subdivisions.Count(s => string.IsNullOrWhiteSpace(s.Code));
    if (withoutCode > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Без кода: {withoutCode}");
        Console.ResetColor();
    }
}

/// Выбор нескольких подразделений: «3», «3,5,7», «10-13», «3, 10-13».
/// «*» — заполнить коды всех подразделений без кода по маске (<paramref name="fillByMask"/>).
static List<SbisSubdivision> ChooseMany(IReadOnlyList<SbisSubdivision> items, out bool fillByMask)
{
    fillByMask = false;
    while (true)
    {
        Console.WriteLine();
        var input = Prompt("Номера подразделений для смены кода (например 3,5,10-13; * — все без кода по маске; пусто — выход): ");
        if (input.Length == 0)
            return [];
        if (input == "*")
        {
            fillByMask = true;
            return [];
        }

        var numbers = ParseNumbers(input, items.Count, out var error);
        if (numbers is not null)
            return numbers.Select(n => items[n - 1]).ToList();

        Console.WriteLine(error);
    }
}

static SortedSet<int>? ParseNumbers(string input, int max, out string error)
{
    var result = new SortedSet<int>();
    foreach (var part in input.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
    {
        var range = part.Split('-', 2);
        if (!int.TryParse(range[0], out var from) || (range.Length == 2 && !int.TryParse(range[1], out _)))
        {
            error = $"Не понимаю «{part}».";
            return null;
        }
        var to = range.Length == 2 ? int.Parse(range[1]) : from;
        if (from < 1 || to > max || from > to)
        {
            error = $"«{part}» вне диапазона 1–{max}.";
            return null;
        }
        for (var n = from; n <= to; n++)
            result.Add(n);
    }
    error = "";
    return result;
}

/// Коды для всех подразделений без кода по маске. Коды не повторяют ни занятые, ни друг друга.
static List<(SbisSubdivision Sub, string NewCode)> BuildMaskChanges(
    IReadOnlyList<SbisSubdivision> all, IReadOnlySet<string> takenCodes, string? maskFromSettings)
{
    var targets = new List<SbisSubdivision>();
    foreach (var sub in all.Where(s => string.IsNullOrWhiteSpace(s.Code)))
    {
        if (Guid.TryParse(sub.ExternalId, out _))
            targets.Add(sub);
        else
            Console.WriteLine($"  {sub.Name}: нет UUID (ВнешнийИдентификатор «{sub.ExternalId}») — пропускаю.");
    }
    if (targets.Count == 0)
        return [];

    CodeMask? mask = null;
    if (!string.IsNullOrWhiteSpace(maskFromSettings) && !CodeMask.TryParse(maskFromSettings, out mask, out var settingsError))
        Console.WriteLine($"Маска «{maskFromSettings}» из SBIS_CODE_MASK не подходит: {settingsError}");

    while (mask is null)
    {
        var input = Prompt("Маска кода (# — номер с ведущими нулями, например БТ###; пусто — отмена): ");
        if (input.Length == 0)
            return [];
        if (!CodeMask.TryParse(input, out mask, out var error))
            Console.WriteLine(error);
    }

    Console.WriteLine($"Маска: {mask}");
    var taken = new HashSet<string>(takenCodes, StringComparer.OrdinalIgnoreCase);
    var codes = mask.Generate(targets.Count, taken);
    return targets.Zip(codes).ToList();
}

/// Спрашивает новый код для каждого подразделения. Пусто — не менять.
/// Не даёт задать код, который уже занят (в том числе введённый выше для другого подразделения).
static List<(SbisSubdivision Sub, string NewCode)> AskNewCodes(
    IReadOnlyList<SbisSubdivision> selected, IReadOnlySet<string> takenCodes)
{
    var taken = new HashSet<string>(takenCodes, StringComparer.OrdinalIgnoreCase);
    Console.WriteLine("Введите новый код для каждого подразделения (пусто — не менять):");
    var changes = new List<(SbisSubdivision Sub, string NewCode)>();

    foreach (var sub in selected)
    {
        // sabyDepartment.* принимает только UUID — он приходит в поле «ВнешнийИдентификатор».
        if (!Guid.TryParse(sub.ExternalId, out _))
        {
            Console.WriteLine($"  {sub.Name}: нет UUID (ВнешнийИдентификатор «{sub.ExternalId}») — пропускаю.");
            continue;
        }

        while (true)
        {
            var newCode = Prompt($"  {sub.Name} [{(string.IsNullOrWhiteSpace(sub.Code) ? "нет кода" : sub.Code)}]: ");
            if (newCode.Length == 0 || newCode == sub.Code)
                break;

            // Защита от искажённого ввода (например, потерянной кодировки).
            if (newCode.Any(char.IsControl) || newCode.Contains('�'))
            {
                Console.WriteLine("    Код содержит непечатаемые символы — похоже, ввод искажён. Введите ещё раз.");
                continue;
            }

            if (taken.Contains(newCode))
            {
                Console.WriteLine($"    Код «{newCode}» уже занят. Введите другой.");
                continue;
            }

            taken.Add(newCode);
            changes.Add((sub, newCode));
            break;
        }
    }
    return changes;
}

/// Записывает коды:
/// 1) одним sabyDepartment.Read проверяет, что UUID существует и это то же подразделение
///    (название и код совпадают) — иначе Update мог бы создать новое;
/// 2) меняет код через sabyDepartment.Update;
/// 3) одним Read проверяет, что код сохранился.
static async Task ApplyAsync(
    List<(SbisSubdivision Sub, string NewCode)> changes, SbisDepartmentService departments, CancellationToken ct)
{
    var before = await departments.ReadAsync(changes.Select(c => c.Sub.ExternalId!), ct);

    var written = new List<(SbisSubdivision Sub, string NewCode)>();
    foreach (var change in changes)
    {
        var (sub, newCode) = change;
        Console.Write($"  {sub.Name}: ");

        string? problem = null;
        if (!before.TryGetValue(sub.ExternalId!, out var dep))
            problem = $"UUID {sub.ExternalId} не найден через sabyDepartment.Read";
        else if (dep.Title != sub.Name)
            problem = $"по UUID найдено другое подразделение: «{dep.Title}»";
        else if (Normalize(dep.Code) != Normalize(sub.Code))
            problem = $"код в СБИС «{dep.Code}», а в списке «{sub.Code}» — данные изменились, обновите список";

        if (problem is not null)
        {
            // Одна проблемная запись не должна останавливать остальные.
            Console.WriteLine($"пропускаю — {problem}");
            continue;
        }

        try
        {
            await departments.UpdateCodeAsync(dep!, newCode, ct);
            Console.WriteLine("записано");
            written.Add(change);
        }
        catch (SbisException ex)
        {
            Console.WriteLine($"ошибка — {ex}");
        }
    }

    // Итог по фактическим кодам из СБИС: после изменения подразделения пропадают из таблицы «без кода»,
    // поэтому что поменялось, показываем отдельно.
    var after = written.Count > 0
        ? await departments.ReadAsync(written.Select(w => w.Sub.ExternalId!), ct)
        : new Dictionary<string, SbisDepartment>();

    var ok = 0;
    Console.WriteLine();
    Console.WriteLine("Что поменялось:");
    foreach (var (sub, newCode) in changes)
    {
        var wasWritten = written.Any(w => ReferenceEquals(w.Sub, sub));
        var actual = after.TryGetValue(sub.ExternalId!, out var d) ? d.Code : null;
        var success = wasWritten && actual == newCode;
        if (success)
            ok++;

        var status = success ? "изменено"
            : !wasWritten ? "не изменено"
            : $"СБИС принял запрос, но код сейчас «{actual}»";
        Console.ForegroundColor = success ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {sub.Name}: «{Normalize(sub.Code)}» → «{(success ? actual : newCode)}» — {status}");
        Console.ResetColor();
    }
    Console.WriteLine($"Изменено и проверено: {ok} из {changes.Count}.");

    static string Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
}

static bool Confirm(string label) =>
    Prompt(label).ToLowerInvariant() is "да" or "д" or "y" or "yes";

static string Prompt(string label)
{
    Console.Write(label);
    return Console.ReadLine()?.Trim('﻿', ' ', '\t') ?? string.Empty;
}

static string PromptPassword(string label)
{
    Console.Write(label);
    if (Console.IsInputRedirected)
        return Console.ReadLine()?.TrimStart('﻿') ?? string.Empty;

    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
            {
                sb.Length--;
                Console.Write("\b \b");
            }
            continue;
        }
        if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
    }
    Console.WriteLine();
    return sb.ToString();
}

static string Mask(string value) =>
    value.Length <= 8 ? new string('*', value.Length) : $"{value[..4]}…{value[^4..]}";

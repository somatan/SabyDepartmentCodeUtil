using System.Text.Json;

namespace ServiceCodeChanger.Sbis;

/// <summary>Справочники СБИС: наши организации и их подразделения.</summary>
public sealed class SbisDirectoryService
{
    private const string ServiceEndpoint = "/service/?srv=1";
    private const int PageSize = 100;
    private const int MaxPages = 1000; // защита от бесконечного цикла, если сервер всегда отвечает «ЕстьЕще: Да»

    private readonly SbisRpcClient _rpc;

    public SbisDirectoryService(SbisRpcClient rpc) => _rpc = rpc;

    public async Task<IReadOnlyList<SbisOrganization>> ListOurOrganizationsAsync(CancellationToken ct = default)
    {
        var list = new List<SbisOrganization>();
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await _rpc.CallAsync(
                ServiceEndpoint,
                "СБИС.СписокНашихОрганизаций",
                // У этого метода нет обёртки «Параметр»: params = { Фильтр: { …, Навигация } }.
                new { Фильтр = new { Навигация = Navigation(page) } },
                ct);

            foreach (var item in result.Items("НашаОрганизация"))
            {
                var org = ParseOrganization(item);
                if (org is not null)
                    list.Add(org);
            }

            if (!HasMore(result))
                break;
        }
        return list;
    }

    public async Task<IReadOnlyList<SbisSubdivision>> ListSubdivisionsAsync(
        SbisOrganization organization, CancellationToken ct = default)
    {
        object ourOrganization = organization.IsIndividual
            ? new { СвФЛ = new { ИНН = organization.Inn } }
            : new { СвЮЛ = new { ИНН = organization.Inn, КПП = organization.Kpp } };

        var list = new List<SbisSubdivision>();
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await _rpc.CallAsync(
                ServiceEndpoint,
                "СБИС.СписокПодразделений",
                new
                {
                    Параметр = new
                    {
                        Фильтр = new { НашаОрганизация = ourOrganization },
                        Навигация = Navigation(page),
                    },
                },
                ct);

            foreach (var item in result.Items("Подразделение"))
                list.Add(ParseSubdivision(item));

            if (!HasMore(result))
                break;
        }
        return list;
    }

    private static object Navigation(int page) => new
    {
        РазмерСтраницы = PageSize.ToString(),
        Страница = page.ToString(),
    };

    private static bool HasMore(JsonElement result) =>
        string.Equals(result.Obj("Навигация").Str("ЕстьЕще"), "Да", StringComparison.OrdinalIgnoreCase);

    private static SbisOrganization? ParseOrganization(JsonElement item)
    {
        var ul = item.Obj("СвЮЛ");
        if (ul.ValueKind == JsonValueKind.Object && ul.Str("ИНН") is { Length: > 0 } inn)
        {
            return new SbisOrganization(
                ul.Str("Название") ?? "(без названия)",
                inn,
                ul.Str("КПП"),
                ul.Str("КодФилиала"),
                IsIndividual: false);
        }

        var fl = item.Obj("СвФЛ");
        if (fl.ValueKind == JsonValueKind.Object && fl.Str("ИНН") is { Length: > 0 } flInn)
        {
            var name = string.Join(' ', new[] { fl.Str("Фамилия"), fl.Str("Имя"), fl.Str("Отчество") }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            return new SbisOrganization(
                name.Length > 0 ? $"ИП {name}" : "(ИП без названия)",
                flInn,
                Kpp: null,
                BranchCode: null,
                IsIndividual: true);
        }

        return null;
    }

    private static SbisSubdivision ParseSubdivision(JsonElement item)
    {
        // По документации раздел может быть как внутри подразделения, так и рядом с ним.
        var section = item.Obj("Раздел");
        return new SbisSubdivision(
            Id: item.Str("Идентификатор") ?? string.Empty,
            Name: item.Str("Название") ?? "(без названия)",
            Code: item.Str("Код"),
            ExternalId: item.Str("ВнешнийИдентификатор"),
            SectionId: section.Str("Идентификатор"),
            SectionName: section.Str("Название"),
            SectionCode: section.Str("Код"));
    }
}

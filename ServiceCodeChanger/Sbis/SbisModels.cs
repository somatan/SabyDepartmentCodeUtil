namespace ServiceCodeChanger.Sbis;

/// <summary>Наша организация (юрлицо или ИП) из СБИС.СписокНашихОрганизаций.</summary>
public sealed record SbisOrganization(
    string Name,
    string Inn,
    string? Kpp,
    string? BranchCode,
    bool IsIndividual)
{
    public override string ToString() => $"{Name} ({Details(includeInn: true)})";

    /// <summary>Реквизиты в скобках: ИНН (опционально), КПП, код филиала — пустые пропускаются.</summary>
    public string Details(bool includeInn)
    {
        var parts = new List<string>();
        if (includeInn)
            parts.Add($"ИНН {Inn}");
        if (!string.IsNullOrWhiteSpace(Kpp))
            parts.Add($"КПП {Kpp}");
        if (!string.IsNullOrWhiteSpace(BranchCode))
            parts.Add($"код филиала {BranchCode}");
        return string.Join(", ", parts);
    }
}

/// <summary>Подразделение из СБИС.СписокПодразделений.</summary>
public sealed record SbisSubdivision(
    string Id,
    string Name,
    string? Code,
    string? ExternalId,
    string? SectionId,
    string? SectionName,
    string? SectionCode)
{
    /// <summary>
    /// Корень структуры — сама организация: у неё нет раздела (родителя), а у подразделений
    /// верхнего уровня она указана в «Раздел».
    /// </summary>
    public bool IsRoot => string.IsNullOrEmpty(SectionId) && string.IsNullOrEmpty(SectionName);
}

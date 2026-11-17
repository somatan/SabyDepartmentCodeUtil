namespace ServiceCodeChanger.Sbis;

/// <summary>Головная организация и её филиалы (записи с тем же ИНН).</summary>
public sealed record OrganizationNode(SbisOrganization Head, IReadOnlyList<SbisOrganization> Branches);

public static class OrganizationTree
{
    /// <summary>
    /// Группирует плоский список «наших организаций» по ИНН.
    /// Головной считается запись без кода филиала, у которой КПП начинается с тех же 4 цифр, что и ИНН
    /// (код налоговой по месту регистрации юрлица); если такой нет — первая запись без кода филиала;
    /// если и такой нет — первая запись группы.
    /// Порядок групп и филиалов внутри — как в ответе сервера.
    /// </summary>
    public static IReadOnlyList<OrganizationNode> Build(IEnumerable<SbisOrganization> organizations) =>
        organizations
            .GroupBy(o => o.Inn)
            .Select(g =>
            {
                var items = g.ToList();
                var head = items.FirstOrDefault(o => IsWithoutBranchCode(o) && KppMatchesInn(o))
                           ?? items.FirstOrDefault(IsWithoutBranchCode)
                           ?? items[0];
                return new OrganizationNode(head, items.Where(o => !ReferenceEquals(o, head)).ToList());
            })
            .ToList();

    private static bool IsWithoutBranchCode(SbisOrganization o) => string.IsNullOrWhiteSpace(o.BranchCode);

    private static bool KppMatchesInn(SbisOrganization o) =>
        o.Kpp is { Length: >= 4 } kpp && o.Inn.Length >= 4 && kpp[..4] == o.Inn[..4];
}

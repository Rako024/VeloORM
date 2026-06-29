namespace VeloORM.Metadata;

/// <summary>Immutable description of an index over one or more columns.</summary>
public sealed class IndexModel
{
    public IndexModel(string name, IReadOnlyList<ColumnModel> columns, bool isUnique)
    {
        Name = name;
        Columns = columns;
        IsUnique = isUnique;
    }

    public string Name { get; }

    public IReadOnlyList<ColumnModel> Columns { get; }

    public bool IsUnique { get; }
}

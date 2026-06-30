using System.Text;
using VeloORM.Metadata;
using VeloORM.Migrations.Schema;

namespace VeloORM.Scaffold;

public sealed class ScaffoldOptions
{
    public string Namespace { get; init; } = "ScaffoldedModel";
    public string ContextName { get; init; } = "ScaffoldedDbContext";
}

/// <summary>
/// Reverse-engineers C# entity classes (+ a context) from an existing PostgreSQL schema. Column/table
/// names map snake_case → PascalCase; explicit [Table]/[Column] are emitted only when the name differs
/// from the convention, and [Key] marks primary-key columns.
/// </summary>
public sealed class EntityScaffolder
{
    private readonly INamingConvention _naming = SnakeCaseNamingConvention.Instance;
    private readonly ScaffoldOptions _options;

    public EntityScaffolder(ScaffoldOptions? options = null) => _options = options ?? new ScaffoldOptions();

    /// <summary>Returns a map of file name → C# source (one per entity, plus the context).</summary>
    public Dictionary<string, string> Generate(SchemaModel schema)
    {
        var files = new Dictionary<string, string>();
        var classNames = new List<string>();

        foreach (var table in schema.Tables)
        {
            var (className, source) = GenerateEntity(table);
            files[className + ".cs"] = source;
            classNames.Add(className);
        }

        files[_options.ContextName + ".cs"] = GenerateContext(classNames);
        return files;
    }

    public (string ClassName, string Source) GenerateEntity(SchemaTable table)
    {
        var className = Singularize(SnakeCaseNamingConvention.ToPascalCase(table.Name));
        var sb = new StringBuilder();
        sb.AppendLine("using VeloORM.Metadata;").AppendLine();
        sb.Append("namespace ").Append(_options.Namespace).AppendLine(";").AppendLine();

        // [Table] only when the convention would produce a different name/schema.
        if (!string.Equals(_naming.TableName(className), table.Name, StringComparison.Ordinal) || table.Schema is not null and not "public")
        {
            sb.Append("[Table(\"").Append(table.Name).Append('"');
            if (table.Schema is not null and not "public") sb.Append(", Schema = \"").Append(table.Schema).Append('"');
            sb.AppendLine(")]");
        }

        sb.Append("public class ").AppendLine(className);
        sb.AppendLine("{");
        foreach (var column in table.Columns)
        {
            var propName = SnakeCaseNamingConvention.ToPascalCase(column.Name);
            var clrType = ScaffoldTypeMapper.ToClrTypeName(column.StoreType, column.IsNullable);
            bool isKey = table.PrimaryKey.Contains(column.Name, StringComparer.OrdinalIgnoreCase);

            if (isKey) sb.AppendLine("    [Key]");
            if (!string.Equals(_naming.ColumnName(propName), column.Name, StringComparison.Ordinal))
                sb.Append("    [Column(\"").Append(column.Name).AppendLine("\")]");

            sb.Append("    public ").Append(clrType).Append(' ').Append(propName).Append(" { get; set; }");
            if (clrType == "string") sb.Append(" = \"\";"); // non-null string default
            sb.AppendLine();
        }
        sb.AppendLine("}");
        return (className, sb.ToString());
    }

    public string GenerateContext(IReadOnlyList<string> classNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using VeloORM.Metadata;");
        sb.AppendLine("using VeloORM.Runtime;");
        sb.AppendLine("using VeloORM.Postgres;").AppendLine();
        sb.Append("namespace ").Append(_options.Namespace).AppendLine(";").AppendLine();

        sb.Append("public class ").Append(_options.ContextName).AppendLine(" : VeloDbContext");
        sb.AppendLine("{");
        sb.Append("    public ").Append(_options.ContextName).AppendLine("(string connectionString)");
        sb.AppendLine("        : base(BuildModel(), PostgresDialect.Instance,");
        sb.AppendLine("               new NpgsqlConnectionFactory(connectionString),");
        sb.AppendLine("               new PostgresCommandExecutor(new NpgsqlConnectionFactory(connectionString))) { }");
        sb.AppendLine();
        sb.Append("    private static VeloModel BuildModel() => VeloModel.Build(new[] { ");
        sb.Append(string.Join(", ", classNames.Select(c => "typeof(" + c + ")")));
        sb.AppendLine(" });");
        sb.AppendLine();
        foreach (var className in classNames)
            sb.Append("    public IQueryable<").Append(className).Append("> ")
              .Append(Pluralize(className)).Append(" => Set<").Append(className).AppendLine(">();");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Pluralize(string name) =>
        name.EndsWith("y", StringComparison.Ordinal) ? name.Substring(0, name.Length - 1) + "ies" : name + "s";

    private static string Singularize(string name)
    {
        if (name.EndsWith("ies", StringComparison.Ordinal)) return name.Substring(0, name.Length - 3) + "y";
        if (name.EndsWith("s", StringComparison.Ordinal) && !name.EndsWith("ss", StringComparison.Ordinal))
            return name.Substring(0, name.Length - 1);
        return name;
    }
}

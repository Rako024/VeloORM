using System.Reflection;
using System.Runtime.Loader;
using VeloORM.Cli;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;
using VeloORM.Scaffold;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();
var options = ParseOptions(args.Skip(1).ToArray(), out var positional);

string Connection() =>
    options.GetValueOrDefault("connection")
    ?? options.GetValueOrDefault("c")
    ?? Environment.GetEnvironmentVariable("VELO_CONNECTION")
    ?? throw new InvalidOperationException("No connection string. Pass --connection or set VELO_CONNECTION.");

string MigrationsDir() => options.GetValueOrDefault("output") ?? options.GetValueOrDefault("o") ?? "Migrations";

try
{
    switch (command)
    {
        case "add-migration":
        {
            var name = positional.FirstOrDefault() ?? throw new InvalidOperationException("Usage: velo add-migration <name> --assembly <path> [--context <type>]");
            var assemblyPath = options.GetValueOrDefault("assembly") ?? throw new InvalidOperationException("--assembly <path> is required for add-migration.");
            var (model, dialect, factory) = LoadContext(assemblyPath, options.GetValueOrDefault("context"), Connection());
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            CliCommands.AddMigration(model, dialect, factory, MigrationsDir(), name, timestamp, Console.Out);
            break;
        }
        case "update-database":
            CliCommands.UpdateDatabase(new NpgsqlConnectionFactory(Connection()), MigrationsDir(), Console.Out);
            break;
        case "revert":
            CliCommands.Revert(new NpgsqlConnectionFactory(Connection()), MigrationsDir(), Console.Out);
            break;
        case "list-migrations":
            CliCommands.ListMigrations(new NpgsqlConnectionFactory(Connection()), MigrationsDir(), Console.Out);
            break;
        case "scaffold":
        {
            var scaffoldOptions = new ScaffoldOptions
            {
                Namespace = options.GetValueOrDefault("namespace") ?? "ScaffoldedModel",
                ContextName = options.GetValueOrDefault("context") ?? "ScaffoldedDbContext",
            };
            CliCommands.Scaffold(new NpgsqlConnectionFactory(Connection()), scaffoldOptions,
                options.GetValueOrDefault("output") ?? "Scaffolded", Console.Out);
            break;
        }
        default:
            PrintUsage();
            return 1;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static (VeloModel Model, VeloORM.Sql.ISqlDialect Dialect, VeloORM.Data.IConnectionFactory Factory) LoadContext(
    string assemblyPath, string? contextTypeName, string connectionString)
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
    var contextType = contextTypeName is not null
        ? assembly.GetType(contextTypeName) ?? throw new InvalidOperationException($"Context type '{contextTypeName}' not found.")
        : assembly.GetTypes().FirstOrDefault(t => typeof(VeloDbContext).IsAssignableFrom(t) && !t.IsAbstract)
          ?? throw new InvalidOperationException("No VeloDbContext-derived type found in the assembly.");

    var context = (VeloDbContext)(Activator.CreateInstance(contextType, connectionString)
        ?? throw new InvalidOperationException($"Could not instantiate '{contextType.Name}' with a (string connectionString) constructor."));
    return (context.Model, context.Dialect, context.ConnectionFactory);
}

static Dictionary<string, string> ParseOptions(string[] args, out List<string> positional)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    positional = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            options[key] = value;
        }
        else if (args[i].StartsWith("-", StringComparison.Ordinal))
        {
            var key = args[i][1..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal) ? args[++i] : "true";
            options[key] = value;
        }
        else positional.Add(args[i]);
    }
    return options;
}

static void PrintUsage()
{
    Console.WriteLine("""
        velo — VeloORM CLI

        Commands:
          add-migration <name> --assembly <dll> [--context <type>] [--output <dir>] [--connection <cs>]
          update-database [--output <dir>] [--connection <cs>]
          revert [--output <dir>] [--connection <cs>]
          list-migrations [--output <dir>] [--connection <cs>]
          scaffold [--namespace <ns>] [--context <name>] [--output <dir>] [--connection <cs>]

        Connection string: --connection / -c, or the VELO_CONNECTION environment variable.
        Migrations directory defaults to ./Migrations.
        """);
}

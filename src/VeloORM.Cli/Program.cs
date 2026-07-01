using System.Reflection;
using VeloORM.Cli;
using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Runtime;
using VeloORM.Scaffold;
using VeloORM.Sql;

if (args.Length == 0)
{
    HelpText.PrintUsage(Console.Out);
    return 1;
}

var command = args[0].ToLowerInvariant();

// `velo help [command]` and a bare `--help/-h` show usage.
if (command is "help" or "--help" or "-h")
{
    HelpText.PrintCommand(args.Skip(1).FirstOrDefault(), Console.Out);
    return 0;
}

var options = ParseOptions(args.Skip(1).ToArray(), out var positional);
var verbose = options.ContainsKey("verbose");

// Per-command --help.
if (options.ContainsKey("help") || options.ContainsKey("h"))
{
    HelpText.PrintCommand(command, Console.Out);
    return 0;
}

// ---- shared option helpers -------------------------------------------------

// The project directory backs both appsettings discovery and the default migrations location.
// Prefer the .csproj directory (source, where appsettings.json lives); else fall back to the
// directory of an explicitly-supplied assembly (its output dir, where appsettings is copied for
// web apps).
bool hasProject = options.ContainsKey("project") || options.ContainsKey("p");
string? ProjectDirectory()
{
    var project = options.GetValueOrDefault("project") ?? options.GetValueOrDefault("p");
    if (project is not null)
        return Path.GetDirectoryName(Path.GetFullPath(project));
    var assembly = options.GetValueOrDefault("assembly");
    return assembly is not null ? Path.GetDirectoryName(Path.GetFullPath(assembly)) : null;
}

string ResolveConnection(string? projectDir) =>
    ConnectionResolver.Resolve(
        options.GetValueOrDefault("connection")
        ?? options.GetValueOrDefault("c")
        ?? Environment.GetEnvironmentVariable("VELO_CONNECTION"),
        options.GetValueOrDefault("connection-name"),
        projectDir,
        Console.Out);

string MigrationsDir(string? projectDir)
{
    var explicitOut = options.GetValueOrDefault("output") ?? options.GetValueOrDefault("o");
    if (explicitOut is not null)
        return explicitOut;
    // Default under the project directory when --project is used, so migrations land next to the
    // code rather than in the current working directory.
    if (hasProject && projectDir is not null)
        return Path.Combine(projectDir, "Migrations");
    return "Migrations";
}

try
{
    switch (command)
    {
        case "add-migration":
        {
            var name = positional.FirstOrDefault()
                ?? throw new InvalidOperationException("Usage: velo add-migration <name> --project <csproj> | --assembly <dll>");
            var projectDir = ProjectDirectory();
            var assemblyPath = ResolveAssemblyPath(options, verbose);
            var (model, dialect, factory) = LoadContext(
                assemblyPath, options.GetValueOrDefault("context"),
                options.GetValueOrDefault("connection") ?? options.GetValueOrDefault("c") ?? Environment.GetEnvironmentVariable("VELO_CONNECTION"),
                options.GetValueOrDefault("connection-name"), projectDir, Console.Out);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            CliCommands.AddMigration(model, dialect, factory, MigrationsDir(projectDir), name, timestamp, Console.Out);
            break;
        }
        case "update-database":
        {
            var projectDir = ProjectDirectory();
            CliCommands.UpdateDatabase(new NpgsqlConnectionFactory(ResolveConnection(projectDir)), MigrationsDir(projectDir), Console.Out);
            break;
        }
        case "revert":
        {
            var projectDir = ProjectDirectory();
            CliCommands.Revert(new NpgsqlConnectionFactory(ResolveConnection(projectDir)), MigrationsDir(projectDir), Console.Out);
            break;
        }
        case "list-migrations":
        {
            var projectDir = ProjectDirectory();
            CliCommands.ListMigrations(new NpgsqlConnectionFactory(ResolveConnection(projectDir)), MigrationsDir(projectDir), Console.Out);
            break;
        }
        case "scaffold":
        {
            var projectDir = ProjectDirectory();
            var scaffoldOptions = new ScaffoldOptions
            {
                Namespace = options.GetValueOrDefault("namespace") ?? "ScaffoldedModel",
                // For scaffold, --context-name names the generated context class. --context is accepted
                // as an alias for backward compatibility.
                ContextName = options.GetValueOrDefault("context-name") ?? options.GetValueOrDefault("context") ?? "ScaffoldedDbContext",
            };
            CliCommands.Scaffold(new NpgsqlConnectionFactory(ResolveConnection(projectDir)), scaffoldOptions,
                options.GetValueOrDefault("output") ?? options.GetValueOrDefault("o") ?? "Scaffolded", Console.Out,
                force: options.ContainsKey("force"));
            break;
        }
        default:
            Console.Error.WriteLine($"Unknown command: {command}");
            HelpText.PrintUsage(Console.Out);
            return 1;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + FriendlyError.Describe(ex));
    if (verbose)
        Console.Error.WriteLine(ex);
    return 1;
}

// ---- assembly + context loading -------------------------------------------

// Resolves the user assembly path: builds --project (unless --no-build), or uses --assembly directly.
static string ResolveAssemblyPath(Dictionary<string, string> options, bool verbose)
{
    var project = options.GetValueOrDefault("project") ?? options.GetValueOrDefault("p");
    if (project is not null)
    {
        var configuration = options.GetValueOrDefault("configuration") ?? "Debug";
        return ProjectBuilder.BuildAndResolveAssembly(project, configuration, options.ContainsKey("no-build"), Console.Out);
    }

    var assembly = options.GetValueOrDefault("assembly");
    if (assembly is not null)
        return Path.GetFullPath(assembly);

    throw new InvalidOperationException("Pass --project <csproj> (recommended) or --assembly <dll> for add-migration.");
}

static (VeloModel Model, ISqlDialect Dialect, VeloORM.Data.IConnectionFactory Factory) LoadContext(
    string assemblyPath, string? contextTypeName, string? explicitConnection, string? connectionName,
    string? projectDir, TextWriter log)
{
    var loadContext = new UserAssemblyLoadContext(assemblyPath);
    var assembly = loadContext.LoadMain(assemblyPath);

    // 1. A design-time factory takes precedence — it constructs a fully-configured context itself.
    if (FindDesignTimeFactory(assembly, contextTypeName) is { } factory)
    {
        var factoryInstance = Activator.CreateInstance(factory.FactoryType)
            ?? throw new InvalidOperationException($"Could not instantiate design-time factory '{factory.FactoryType.Name}'.");
        var created = factory.CreateMethod.Invoke(factoryInstance, new object?[] { Array.Empty<string>() })
            ?? throw new InvalidOperationException($"Design-time factory '{factory.FactoryType.Name}' returned null.");
        var ctxFromFactory = (VeloDbContext)created;
        return (ctxFromFactory.Model, ctxFromFactory.Dialect, ctxFromFactory.ConnectionFactory);
    }

    var contextType = ResolveContextType(assembly, contextTypeName);

    // 2. (string connectionString) constructor — resolve a connection string for it.
    var stringCtor = contextType.GetConstructor(new[] { typeof(string) });
    if (stringCtor is not null)
    {
        var connection = ConnectionResolver.Resolve(explicitConnection, connectionName, projectDir, log);
        var ctx = (VeloDbContext)stringCtor.Invoke(new object[] { connection });
        return (ctx.Model, ctx.Dialect, ctx.ConnectionFactory);
    }

    // 3. Parameterless constructor — the context configures its own connection.
    var parameterless = contextType.GetConstructor(Type.EmptyTypes);
    if (parameterless is not null)
    {
        var ctx = (VeloDbContext)parameterless.Invoke(null);
        return (ctx.Model, ctx.Dialect, ctx.ConnectionFactory);
    }

    throw new InvalidOperationException(
        $"Cannot construct '{contextType.Name}'. Provide a public (string connectionString) constructor, a " +
        "parameterless constructor, or an IVeloDesignTimeDbContextFactory<> implementation.");
}

static Type ResolveContextType(Assembly assembly, string? contextTypeName)
{
    if (contextTypeName is not null)
        return assembly.GetType(contextTypeName)
            ?? assembly.GetTypes().FirstOrDefault(t => t.Name == contextTypeName)
            ?? throw new InvalidOperationException($"Context type '{contextTypeName}' was not found in the assembly.");

    var contexts = assembly.GetTypes()
        .Where(t => typeof(VeloDbContext).IsAssignableFrom(t) && !t.IsAbstract)
        .ToList();

    return contexts.Count switch
    {
        0 => throw new InvalidOperationException("No VeloDbContext-derived type found in the assembly."),
        1 => contexts[0],
        _ => throw new InvalidOperationException(
            $"Multiple VeloDbContext types found ({string.Join(", ", contexts.Select(t => t.Name))}). " +
            "Choose one with --context <TypeName>."),
    };
}

static (Type FactoryType, MethodInfo CreateMethod)? FindDesignTimeFactory(Assembly assembly, string? contextTypeName)
{
    foreach (var type in assembly.GetTypes())
    {
        if (type.IsAbstract || type.IsInterface)
            continue;
        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IVeloDesignTimeDbContextFactory<>))
                continue;
            var producedContext = iface.GetGenericArguments()[0];
            if (contextTypeName is not null &&
                producedContext.Name != contextTypeName && producedContext.FullName != contextTypeName)
                continue;
            var method = iface.GetMethod(nameof(IVeloDesignTimeDbContextFactory<VeloDbContext>.CreateDbContext));
            if (method is not null)
                return (type, method);
        }
    }
    return null;
}

// ---- argument parsing ------------------------------------------------------

static Dictionary<string, string> ParseOptions(string[] args, out List<string> positional)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    positional = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal) || args[i].StartsWith("-", StringComparison.Ordinal))
        {
            var key = args[i].TrimStart('-');
            // A following token is the value unless it is itself a flag (starts with '-' and is not a
            // lone '-'). Boolean flags get "true".
            var hasValue = i + 1 < args.Length &&
                           !(args[i + 1].Length > 1 && args[i + 1].StartsWith("-", StringComparison.Ordinal));
            options[key] = hasValue ? args[++i] : "true";
        }
        else
        {
            positional.Add(args[i]);
        }
    }
    return options;
}

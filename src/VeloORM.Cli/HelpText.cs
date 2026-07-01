namespace VeloORM.Cli;

/// <summary>Usage and per-command help for the <c>velo</c> CLI.</summary>
public static class HelpText
{
    public static void PrintUsage(TextWriter output) => output.WriteLine(
        """
        velo — VeloORM CLI

        Usage:
          velo <command> [options]
          velo help <command>        Detailed help and examples for a command

        Commands:
          add-migration <name>       Scaffold a migration from the current model
          update-database            Apply pending migrations
          revert                     Revert the last applied migration
          list-migrations            List migrations and their applied state
          scaffold                   Reverse-engineer entities from a live database

        Common options:
          --project, -p <csproj>     Target project; built automatically to find the assembly
          --assembly <dll>           Target assembly (alternative to --project)
          --connection, -c <cs>      Connection string
          --connection-name <key>    Key to read from appsettings.json ConnectionStrings
          --output, -o <dir>         Migrations/scaffold output directory
          --verbose                  Show full exception details on error

        Connection resolution order: --connection / -c > VELO_CONNECTION env var >
        appsettings.json (auto when a single key exists; otherwise use --connection-name).
        With --project, migrations default to <project>/Migrations.
        """);

    public static void PrintCommand(string? command, TextWriter output)
    {
        switch (command?.ToLowerInvariant())
        {
            case "add-migration":
                output.WriteLine(
                    """
                    velo add-migration <name> — scaffold a migration by diffing your model against the database.

                    The target project/assembly is loaded to read the model. The database must be up to date
                    (all existing migrations applied) so the diff is computed against the correct baseline.

                    Context discovery (in order):
                      1. A type implementing IVeloDesignTimeDbContextFactory<TContext> (it supplies its own connection).
                      2. A VeloDbContext with a public (string connectionString) constructor.
                      3. A VeloDbContext with a parameterless constructor.
                    Use --context <TypeName> when more than one context exists.

                    Options:
                      --project, -p <csproj>    Project to build and load (recommended)
                      --assembly <dll>          Assembly to load instead of --project
                      --no-build                Skip building --project
                      --configuration <cfg>     Build configuration for --project (default: Debug)
                      --context <TypeName>      Select the context type when several exist
                      --connection, -c <cs>     Connection string (see resolution order)
                      --connection-name <key>   appsettings.json ConnectionStrings key
                      --output, -o <dir>        Migrations directory (default: <project>/Migrations)

                    Examples:
                      velo add-migration InitialCreate --project ./src/MyApp/MyApp.csproj
                      velo add-migration AddOrders -p ./MyApp.csproj --connection-name Postgres
                    """);
                break;
            case "update-database":
                output.WriteLine(
                    """
                    velo update-database — apply all pending migrations.

                    Options:
                      --project, -p <csproj>    Used to locate appsettings.json and the Migrations folder
                      --connection, -c <cs>     Connection string (see resolution order)
                      --connection-name <key>   appsettings.json ConnectionStrings key
                      --output, -o <dir>        Migrations directory

                    Examples:
                      velo update-database --project ./MyApp.csproj
                      velo update-database --connection "Host=localhost;Database=app;Username=u;Password=p"
                    """);
                break;
            case "revert":
                output.WriteLine(
                    """
                    velo revert — revert the last applied migration.

                    Options:
                      --project, -p <csproj>    Used to locate appsettings.json and the Migrations folder
                      --connection, -c <cs>     Connection string (see resolution order)
                      --connection-name <key>   appsettings.json ConnectionStrings key
                      --output, -o <dir>        Migrations directory

                    Example:
                      velo revert --project ./MyApp.csproj
                    """);
                break;
            case "list-migrations":
                output.WriteLine(
                    """
                    velo list-migrations — list migrations; [X] marks applied ones.

                    Options:
                      --project, -p <csproj>    Used to locate appsettings.json and the Migrations folder
                      --connection, -c <cs>     Connection string (see resolution order)
                      --connection-name <key>   appsettings.json ConnectionStrings key
                      --output, -o <dir>        Migrations directory

                    Example:
                      velo list-migrations --project ./MyApp.csproj
                    """);
                break;
            case "scaffold":
                output.WriteLine(
                    """
                    velo scaffold — reverse-engineer entity classes and a context from a live database.

                    Options:
                      --connection, -c <cs>     Connection string (see resolution order)
                      --connection-name <key>   appsettings.json ConnectionStrings key
                      --namespace <ns>          Namespace for generated code (default: ScaffoldedModel)
                      --context-name <name>     Generated context class name (default: ScaffoldedDbContext)
                      --output, -o <dir>        Output directory (default: Scaffolded)
                      --force                   Overwrite existing files (default: skip them)

                    Example:
                      velo scaffold -c "Host=localhost;Database=app;Username=u;Password=p" --namespace MyApp.Data
                    """);
                break;
            default:
                PrintUsage(output);
                break;
        }
    }
}

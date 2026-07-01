using System.Net.Sockets;
using System.Reflection;
using Npgsql;

namespace VeloORM.Cli;

/// <summary>
/// Translates the exceptions users actually hit into short, actionable messages, so the CLI shows
/// guidance instead of a raw stack trace. The full exception is still available via <c>--verbose</c>.
/// </summary>
public static class FriendlyError
{
    /// <summary>Returns a human-friendly, single-or-few-line description of <paramref name="ex"/>.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        PostgresException pg => DescribePostgres(pg),
        NpgsqlException => "Could not connect to PostgreSQL. Check the host, port, and that the server " +
                           "is running, then verify the connection string." + Detail(ex),
        SocketException => "Could not reach the database server (connection refused or host unreachable). " +
                           "Check the host/port and that PostgreSQL is running." + Detail(ex),
        ReflectionTypeLoadException rtl => DescribeTypeLoad(rtl),
        BadImageFormatException => "The target file is not a valid .NET assembly (or targets a different " +
                                   "architecture). Point --project/--assembly at a managed project/DLL." + Detail(ex),
        FileNotFoundException fnf => DescribeFileNotFound(fnf),
        _ => ex.Message,
    };

    private static string DescribePostgres(PostgresException pg) => pg.SqlState switch
    {
        // invalid_catalog_name — database does not exist.
        "3D000" => $"Database does not exist: {pg.MessageText}. Create it, or fix the Database in the " +
                   "connection string.",
        // undefined_table.
        "42P01" => $"Table does not exist: {pg.MessageText}. Run 'velo update-database' to apply migrations first.",
        // invalid_password / invalid_authorization_specification.
        "28P01" or "28000" => "Authentication failed. Check the username and password in the connection string.",
        _ => $"PostgreSQL error {pg.SqlState}: {pg.MessageText}",
    };

    private static string DescribeTypeLoad(ReflectionTypeLoadException rtl)
    {
        var loaderMessages = (rtl.LoaderExceptions ?? Array.Empty<Exception?>())
            .Where(e => e is not null)
            .Select(e => e!.Message)
            .Distinct()
            .Take(5);
        return "Could not load types from the target assembly. This usually means a dependency failed to " +
               "resolve. If this is a Web project, ensure it builds and targets a shared framework the SDK " +
               "provides." + Environment.NewLine + string.Join(Environment.NewLine, loaderMessages);
    }

    private static string DescribeFileNotFound(FileNotFoundException fnf)
    {
        var missing = string.IsNullOrEmpty(fnf.FileName) ? "" : $" ({fnf.FileName})";
        return $"A required file or assembly could not be found{missing}. If loading a dependency failed, " +
               "make sure the target project builds and its output is up to date." + Detail(fnf);
    }

    private static string Detail(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? "" : Environment.NewLine + "Detail: " + ex.Message;
}

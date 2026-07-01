using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeloORM.Cli;

/// <summary>
/// Resolves the connection string for a CLI command when it is not given explicitly. Order of
/// precedence (highest first): an explicit value (<c>--connection</c> or <c>VELO_CONNECTION</c>,
/// resolved by the caller) &gt; <c>appsettings.json</c> in the target project directory.
/// <para>
/// From <c>appsettings.json</c> the key is chosen as: <c>--connection-name</c> if given; else the
/// only key when there is exactly one; else, as a last resort, a key detected by scanning the
/// project's <c>Program.cs</c>/<c>Startup.cs</c> for <c>GetConnectionString("...")</c>; otherwise the
/// user is asked to disambiguate with <c>--connection-name</c>.
/// </para>
/// </summary>
public static class ConnectionResolver
{
    public static string Resolve(string? explicitConnection, string? connectionName, string? projectDirectory, TextWriter log)
    {
        if (!string.IsNullOrWhiteSpace(explicitConnection))
            return explicitConnection!;

        if (projectDirectory is null)
            throw new InvalidOperationException(
                "No connection string. Pass --connection, set VELO_CONNECTION, or use --project so the " +
                "connection can be read from appsettings.json.");

        var connStrings = ReadConnectionStrings(projectDirectory);
        if (connStrings.Count == 0)
            throw new InvalidOperationException(
                $"No connection string. Add one to the ConnectionStrings section of appsettings.json in " +
                $"'{projectDirectory}', or pass --connection.");

        if (!string.IsNullOrWhiteSpace(connectionName))
        {
            if (connStrings.TryGetValue(connectionName!, out var byName))
            {
                log.WriteLine($"Using connection '{connectionName}' from appsettings.json.");
                return byName;
            }
            throw new InvalidOperationException(
                $"Connection name '{connectionName}' not found in appsettings.json. Available: {string.Join(", ", connStrings.Keys)}.");
        }

        if (connStrings.Count == 1)
        {
            var only = connStrings.First();
            log.WriteLine($"Using connection '{only.Key}' from appsettings.json.");
            return only.Value;
        }

        // Multiple keys: last-resort heuristic — detect the key the app actually uses.
        var scanned = ScanSourceForConnectionName(projectDirectory);
        if (scanned is not null && connStrings.TryGetValue(scanned, out var scannedValue))
        {
            log.WriteLine($"Using connection '{scanned}' (detected in source) from appsettings.json.");
            return scannedValue;
        }

        throw new InvalidOperationException(
            $"Multiple connection strings found in appsettings.json ({string.Join(", ", connStrings.Keys)}). " +
            "Choose one with --connection-name <key>.");
    }

    /// <summary>Reads and merges the <c>ConnectionStrings</c> section from <c>appsettings.json</c> and
    /// the environment-specific <c>appsettings.{Environment}.json</c> (environment override wins).</summary>
    public static Dictionary<string, string> ReadConnectionStrings(string projectDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var environment =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Development";

        // Base first, then the environment-specific file so it overrides.
        MergeFrom(Path.Combine(projectDirectory, "appsettings.json"), result);
        MergeFrom(Path.Combine(projectDirectory, $"appsettings.{environment}.json"), result);
        return result;
    }

    private static void MergeFrom(string path, Dictionary<string, string> into)
    {
        if (!File.Exists(path))
            return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("ConnectionStrings", out var section) ||
                section.ValueKind != JsonValueKind.Object)
                return;

            foreach (var entry in section.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { } value)
                    into[entry.Name] = value;
        }
        catch (JsonException)
        {
            // Ignore malformed appsettings; treat as "no connection strings here".
        }
    }

    private static readonly Regex GetConnectionStringCall =
        new("""GetConnectionString\(\s*"([^"]+)"\s*\)""", RegexOptions.Compiled);

    /// <summary>Scans top-level <c>Program.cs</c>/<c>Startup.cs</c> for a single
    /// <c>GetConnectionString("key")</c> call and returns the key when unambiguous.</summary>
    private static string? ScanSourceForConnectionName(string projectDirectory)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "Program.cs", "Startup.cs" })
        {
            var path = Path.Combine(projectDirectory, file);
            if (!File.Exists(path))
                continue;
            foreach (Match m in GetConnectionStringCall.Matches(File.ReadAllText(path)))
                names.Add(m.Groups[1].Value);
        }
        return names.Count == 1 ? names.First() : null;
    }
}

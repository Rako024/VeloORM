using VeloORM.Cli;

namespace VeloORM.Tests.Unit;

public sealed class ConnectionResolverTests : IDisposable
{
    private readonly string _dir;

    public ConnectionResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "velo-conn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteAppSettings(string json) =>
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), json);

    [Fact]
    public void Explicit_connection_wins_over_appsettings()
    {
        WriteAppSettings("""{ "ConnectionStrings": { "Default": "Host=fromfile" } }""");
        var result = ConnectionResolver.Resolve("Host=explicit", connectionName: null, _dir, TextWriter.Null);
        Assert.Equal("Host=explicit", result);
    }

    [Fact]
    public void Single_key_is_auto_selected()
    {
        WriteAppSettings("""{ "ConnectionStrings": { "velo": "Host=solo" } }""");
        var result = ConnectionResolver.Resolve(null, connectionName: null, _dir, TextWriter.Null);
        Assert.Equal("Host=solo", result);
    }

    [Fact]
    public void Connection_name_selects_the_matching_key()
    {
        WriteAppSettings("""{ "ConnectionStrings": { "a": "Host=aaa", "b": "Host=bbb" } }""");
        var result = ConnectionResolver.Resolve(null, connectionName: "b", _dir, TextWriter.Null);
        Assert.Equal("Host=bbb", result);
    }

    [Fact]
    public void Multiple_keys_without_name_and_no_source_hint_throws()
    {
        WriteAppSettings("""{ "ConnectionStrings": { "a": "Host=aaa", "b": "Host=bbb" } }""");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConnectionResolver.Resolve(null, connectionName: null, _dir, TextWriter.Null));
        Assert.Contains("--connection-name", ex.Message);
    }

    [Fact]
    public void Multiple_keys_resolved_by_scanning_program_cs()
    {
        WriteAppSettings("""{ "ConnectionStrings": { "a": "Host=aaa", "chosen": "Host=picked" } }""");
        File.WriteAllText(Path.Combine(_dir, "Program.cs"),
            "var cs = builder.Configuration.GetConnectionString(\"chosen\")!;");
        var result = ConnectionResolver.Resolve(null, connectionName: null, _dir, TextWriter.Null);
        Assert.Equal("Host=picked", result);
    }

    [Fact]
    public void Unknown_connection_name_throws_listing_available()
    {
        WriteAppSettings("""{ "ConnectionStrings": { "a": "Host=aaa" } }""");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConnectionResolver.Resolve(null, connectionName: "missing", _dir, TextWriter.Null));
        Assert.Contains("a", ex.Message);
    }

    [Fact]
    public void No_project_directory_and_no_explicit_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConnectionResolver.Resolve(null, connectionName: null, projectDirectory: null, TextWriter.Null));
        Assert.Contains("VELO_CONNECTION", ex.Message);
    }
}

using System.Net.Sockets;
using Npgsql;
using VeloORM.Cli;

namespace VeloORM.Tests.Unit;

public sealed class FriendlyErrorTests
{
    [Fact]
    public void Missing_database_is_explained()
    {
        var pg = new PostgresException("database \"app\" does not exist", "FATAL", "FATAL", "3D000");
        var message = FriendlyError.Describe(pg);
        Assert.Contains("Database does not exist", message);
    }

    [Fact]
    public void Undefined_table_points_to_update_database()
    {
        var pg = new PostgresException("relation \"products\" does not exist", "ERROR", "ERROR", "42P01");
        var message = FriendlyError.Describe(pg);
        Assert.Contains("update-database", message);
    }

    [Fact]
    public void Bad_password_is_explained()
    {
        var pg = new PostgresException("password authentication failed", "FATAL", "FATAL", "28P01");
        var message = FriendlyError.Describe(pg);
        Assert.Contains("Authentication failed", message);
    }

    [Fact]
    public void Socket_failure_mentions_server_reachability()
    {
        var message = FriendlyError.Describe(new SocketException(10061));
        Assert.Contains("database server", message);
    }

    [Fact]
    public void Unknown_exception_falls_back_to_message()
    {
        var message = FriendlyError.Describe(new InvalidOperationException("custom message"));
        Assert.Equal("custom message", message);
    }
}

using VeloORM.Metadata;
using VeloORM.Postgres;
using VeloORM.Query;
using VeloORM.Runtime;

namespace VeloORM.Tests.Integration;

[Table("time_rows")]
public class TimeRow
{
    public int Id { get; set; }
    [UtcDateTime] public DateTime StampedUtc { get; set; }
    public DateTime PlainStamp { get; set; }
}

[Collection(PostgresCollection.Name)]
public class DateTimeUtcTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private NpgsqlConnectionFactory _factory = null!;
    private PostgresCommandExecutor _executor = null!;

    public DateTimeUtcTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _factory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        _executor = new PostgresCommandExecutor(_factory);
        // Both columns are `timestamp` WITHOUT time zone — the type that rejects Kind=Utc writes.
        await _executor.ExecuteAsync(new SqlStatement(
            """
            DROP TABLE IF EXISTS time_rows;
            CREATE TABLE time_rows (
                id           integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                stamped_utc  timestamp NOT NULL,
                plain_stamp  timestamp NOT NULL
            );
            """,
            Array.Empty<SqlParameterBinding>()));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Utc_DateTime_Writes_To_Timestamp_Column_And_RoundTrips()
    {
        // A Kind=Utc value — previously this threw "Cannot write DateTime with Kind=Utc to
        // timestamp without time zone". The executor now relabels it Unspecified on write.
        var utc = DateTime.SpecifyKind(new DateTime(2026, 6, 29, 12, 30, 45), DateTimeKind.Utc);

        await _executor.ExecuteAsync(new SqlStatement(
            "INSERT INTO time_rows (stamped_utc, plain_stamp) VALUES ($1, $2)",
            new SqlParameterBinding[] { new(utc, typeof(DateTime)), new(utc, typeof(DateTime)) }));

        await using var db = new VeloDbContext(
            VeloModel.Build([typeof(TimeRow)]), PostgresDialect.Instance, _factory, _executor);

        var row = Assert.Single(db.Set<TimeRow>().ToList());

        // Same instant round-tripped for both columns.
        Assert.Equal(utc.Ticks, row.StampedUtc.Ticks);
        Assert.Equal(utc.Ticks, row.PlainStamp.Ticks);

        // [UtcDateTime] column is stamped Utc on read; the plain column is not.
        Assert.Equal(DateTimeKind.Utc, row.StampedUtc.Kind);
        Assert.Equal(DateTimeKind.Unspecified, row.PlainStamp.Kind);
    }
}

using System.Data.Common;
using Npgsql;
using VeloORM.Data;
using VeloORM.Materialization;
using VeloORM.Query;

namespace VeloORM.Postgres;

/// <summary>
/// Executes <see cref="SqlStatement"/>s against PostgreSQL. Bound parameters are added as Npgsql
/// positional parameters (matching the dialect's <c>$N</c> placeholders); nulls carry an explicit
/// <c>NpgsqlDbType</c> so the server never has to guess. Without a transaction a fresh pooled
/// connection is opened and disposed per call; with a transaction the command runs on that
/// transaction's connection, which is left open for the lifetime of the transaction.
/// </summary>
public sealed class PostgresCommandExecutor : ICommandExecutor
{
    private readonly IConnectionFactory _connectionFactory;

    public PostgresCommandExecutor(IConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    /// <inheritdoc />
    public Action<string>? CommandLogger { get; set; }

    // ---- sync ----------------------------------------------------------

    public List<T> Query<T>(SqlStatement statement, IMaterializer<T> materializer) =>
        Query(statement, materializer, null);

    public List<T> Query<T>(SqlStatement statement, IMaterializer<T> materializer, DbTransaction? transaction)
    {
        var (connection, owned) = Lease(transaction);
        try
        {
            using var command = CreateCommand(connection, statement, transaction as NpgsqlTransaction);
            using var reader = command.ExecuteReader();
            var results = new List<T>();
            while (reader.Read())
                results.Add(materializer.Read(reader));
            return results;
        }
        finally { if (owned) connection.Dispose(); }
    }

    public int Execute(SqlStatement statement) => Execute(statement, null);

    public int Execute(SqlStatement statement, DbTransaction? transaction)
    {
        var (connection, owned) = Lease(transaction);
        try
        {
            using var command = CreateCommand(connection, statement, transaction as NpgsqlTransaction);
            return command.ExecuteNonQuery();
        }
        finally { if (owned) connection.Dispose(); }
    }

    public TScalar? ExecuteScalar<TScalar>(SqlStatement statement) => ExecuteScalar<TScalar>(statement, null);

    public TScalar? ExecuteScalar<TScalar>(SqlStatement statement, DbTransaction? transaction)
    {
        var (connection, owned) = Lease(transaction);
        try
        {
            using var command = CreateCommand(connection, statement, transaction as NpgsqlTransaction);
            return Coerce<TScalar>(command.ExecuteScalar());
        }
        finally { if (owned) connection.Dispose(); }
    }

    // ---- async ---------------------------------------------------------

    public Task<List<T>> QueryAsync<T>(SqlStatement statement, IMaterializer<T> materializer, CancellationToken cancellationToken = default) =>
        QueryAsync(statement, materializer, null, cancellationToken);

    public async Task<List<T>> QueryAsync<T>(SqlStatement statement, IMaterializer<T> materializer, DbTransaction? transaction, CancellationToken cancellationToken = default)
    {
        var (connection, owned) = await LeaseAsync(transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(connection, statement, transaction as NpgsqlTransaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(materializer.Read(reader));
            return results;
        }
        finally { if (owned) await connection.DisposeAsync().ConfigureAwait(false); }
    }

    public Task<int> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken = default) =>
        ExecuteAsync(statement, null, cancellationToken);

    public async Task<int> ExecuteAsync(SqlStatement statement, DbTransaction? transaction, CancellationToken cancellationToken = default)
    {
        var (connection, owned) = await LeaseAsync(transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(connection, statement, transaction as NpgsqlTransaction);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { if (owned) await connection.DisposeAsync().ConfigureAwait(false); }
    }

    public Task<TScalar?> ExecuteScalarAsync<TScalar>(SqlStatement statement, CancellationToken cancellationToken = default) =>
        ExecuteScalarAsync<TScalar>(statement, null, cancellationToken);

    public async Task<TScalar?> ExecuteScalarAsync<TScalar>(SqlStatement statement, DbTransaction? transaction, CancellationToken cancellationToken = default)
    {
        var (connection, owned) = await LeaseAsync(transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = CreateCommand(connection, statement, transaction as NpgsqlTransaction);
            return Coerce<TScalar>(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { if (owned) await connection.DisposeAsync().ConfigureAwait(false); }
    }

    // ---- compiled-query (typed, boxing-free) path ----------------------

    public List<T> QueryBound<T>(string sql, IMaterializer<T> materializer, Action<ITypedParameterSink> bindParameters)
    {
        using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        connection.Open();
        using var command = CreateBoundCommand(connection, sql, bindParameters);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
            results.Add(materializer.Read(reader));
        return results;
    }

    public TScalar? ExecuteScalarBound<TScalar>(string sql, Action<ITypedParameterSink> bindParameters)
    {
        using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        connection.Open();
        using var command = CreateBoundCommand(connection, sql, bindParameters);
        return Coerce<TScalar>(command.ExecuteScalar());
    }

    private NpgsqlCommand CreateBoundCommand(NpgsqlConnection connection, string sql, Action<ITypedParameterSink> bindParameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        bindParameters(new TypedParameterSink(command));
        Log(sql, command.Parameters.Count);
        return command;
    }

    /// <summary>Emits the parameterized SQL to the logger. Values are bound ($N), so they never appear
    /// in the text — masking is structural. Only the bound parameter count is appended.</summary>
    private void Log(string sql, int parameterCount)
    {
        var logger = CommandLogger;
        if (logger is null) return;
        logger(parameterCount > 0 ? $"{sql} -- {parameterCount} param(s)" : sql);
    }

    /// <summary>Adds strongly-typed Npgsql parameters. Non-null, non-enum values use
    /// <c>NpgsqlParameter&lt;T&gt;</c> (no boxing); enums bind their underlying integral value and nulls
    /// bind <c>DBNull</c> with the mapped type.</summary>
    private sealed class TypedParameterSink : ITypedParameterSink
    {
        private readonly NpgsqlCommand _command;
        public TypedParameterSink(NpgsqlCommand command) => _command = command;

        public void Add<T>(T value)
        {
            if (value is null)
            {
                var p = new NpgsqlParameter { Value = DBNull.Value };
                var clr = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                if (PostgresTypeMapper.GetNpgsqlDbType(clr) is { } dbType)
                    p.NpgsqlDbType = dbType;
                _command.Parameters.Add(p);
                return;
            }

            if (typeof(T).IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(typeof(T));
                _command.Parameters.Add(new NpgsqlParameter
                {
                    Value = Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture),
                });
                return;
            }

            _command.Parameters.Add(new NpgsqlParameter<T> { TypedValue = value });
        }
    }

    // ---- connection leasing -------------------------------------------

    /// <summary>Returns the connection to use and whether this executor owns (must dispose) it.
    /// With a transaction we reuse its open connection and never dispose it.</summary>
    private (NpgsqlConnection Connection, bool Owned) Lease(DbTransaction? transaction)
    {
        if (transaction is not null)
            return ((NpgsqlConnection)transaction.Connection!, false);
        var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        connection.Open();
        return (connection, true);
    }

    private async Task<(NpgsqlConnection Connection, bool Owned)> LeaseAsync(DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null)
            return ((NpgsqlConnection)transaction.Connection!, false);
        var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (connection, true);
    }

    private static TScalar? Coerce<TScalar>(object? result)
    {
        if (result is null || result is DBNull)
            return default;
        return (TScalar)Convert.ChangeType(result, typeof(TScalar), System.Globalization.CultureInfo.InvariantCulture);
    }

    private NpgsqlCommand CreateCommand(NpgsqlConnection connection, SqlStatement statement, NpgsqlTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.CommandText = statement.Sql;
        if (transaction is not null)
            command.Transaction = transaction;
        Log(statement.Sql, statement.Parameters.Count);

        foreach (var binding in statement.Parameters)
        {
            // Unnamed parameters put Npgsql into positional mode, matching $1, $2, ... placeholders.
            var parameter = new NpgsqlParameter();
            BindValue(parameter, binding);
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static void BindValue(NpgsqlParameter parameter, SqlParameterBinding binding)
    {
        var value = binding.Value;

        if (value is null)
        {
            parameter.Value = DBNull.Value;
            if (PostgresTypeMapper.GetNpgsqlDbType(binding.ClrType) is { } dbType)
                parameter.NpgsqlDbType = dbType;
            return;
        }

        // Store enums by their underlying integral value.
        if (value is Enum)
        {
            var underlying = Enum.GetUnderlyingType(value.GetType());
            parameter.Value = Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        parameter.Value = value;
    }
}

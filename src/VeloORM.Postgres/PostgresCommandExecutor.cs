using System.Data.Common;
using Npgsql;
using VeloORM.Data;
using VeloORM.Materialization;
using VeloORM.Query;

namespace VeloORM.Postgres;

/// <summary>
/// Executes <see cref="SqlStatement"/>s against PostgreSQL. Bound parameters are added as Npgsql
/// positional parameters (matching the dialect's <c>$N</c> placeholders); nulls carry an explicit
/// <c>NpgsqlDbType</c> so the server never has to guess. Connections are opened from the factory
/// (Npgsql-pooled) and disposed per call.
/// </summary>
public sealed class PostgresCommandExecutor : ICommandExecutor
{
    private readonly IConnectionFactory _connectionFactory;

    public PostgresCommandExecutor(IConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<List<T>> QueryAsync<T>(
        SqlStatement statement,
        IMaterializer<T> materializer,
        CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, statement);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(materializer.Read(reader));
        return results;
    }

    public List<T> Query<T>(SqlStatement statement, IMaterializer<T> materializer)
    {
        using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        connection.Open();
        using var command = CreateCommand(connection, statement);
        using var reader = command.ExecuteReader();

        var results = new List<T>();
        while (reader.Read())
            results.Add(materializer.Read(reader));
        return results;
    }

    public int Execute(SqlStatement statement)
    {
        using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        connection.Open();
        using var command = CreateCommand(connection, statement);
        return command.ExecuteNonQuery();
    }

    public TScalar? ExecuteScalar<TScalar>(SqlStatement statement)
    {
        using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        connection.Open();
        using var command = CreateCommand(connection, statement);
        var result = command.ExecuteScalar();
        if (result is null || result is DBNull)
            return default;
        return (TScalar)Convert.ChangeType(result, typeof(TScalar), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, statement);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TScalar?> ExecuteScalarAsync<TScalar>(
        SqlStatement statement,
        CancellationToken cancellationToken = default)
    {
        await using var connection = (NpgsqlConnection)_connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, statement);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null || result is DBNull)
            return default;
        return (TScalar)Convert.ChangeType(result, typeof(TScalar), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, SqlStatement statement)
    {
        var command = connection.CreateCommand();
        command.CommandText = statement.Sql;

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

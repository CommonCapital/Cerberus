using Npgsql;

namespace Cerberus.Tests;

/// <summary>
/// The PostgreSQL server the tests may use: CERBERUS_TEST_POSTGRES_URL, a server address without a database
/// (e.g. <c>postgres://cerberus@127.0.0.1:5439</c>). Tests that need one are skipped when it is not set.
/// </summary>
public static class PostgresTestServer
{
    public static string? Url => ApiHost.PostgresServer;

    /// <summary>The address of a new, empty database name on that server (created by whoever connects first).</summary>
    public static string NewDatabase(out string name)
    {
        name = "cerberus_t_" + Guid.NewGuid().ToString("n");
        return $"{Url}/{name}";
    }

    public static NpgsqlConnection Open(string database)
    {
        var settings = new NpgsqlConnectionStringBuilder(new Cerberus.Api.Hosting.PostgresOptions($"{Url}/{database}").ConnectionString()) { Pooling = false };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        connection.Open();
        return connection;
    }

    public static void Drop(string server, string database)
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(
                new Cerberus.Api.Hosting.PostgresOptions($"{server}/postgres").ConnectionString()) { Pooling = false }.ConnectionString);
            connection.Open();
            using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
            drop.ExecuteNonQuery();
        }
        catch (NpgsqlException)
        {
            // Left behind on a server that went away first; its name says what it was.
        }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresTestServer.Url is null) Skip = "Set CERBERUS_TEST_POSTGRES_URL to a PostgreSQL server (postgres://user@host:port) to run these tests.";
    }
}

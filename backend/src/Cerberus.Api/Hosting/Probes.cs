using Cerberus.Api.Cluster;
using Cerberus.Api.Jobs;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace Cerberus.Api.Hosting;

/// <param name="Url">
/// Where PostgreSQL is: <c>postgres://user:password@host[:port][/database][?sslmode=require]</c> (CERBERUS_POSTGRES_URL).
/// The database is <c>cerberus</c> unless named, and is created if it does not exist. Never logged or shown.
/// </param>
public sealed record PostgresOptions(string? Url)
{
    public const string Variable = "CERBERUS_POSTGRES_URL";
    public const string DefaultDatabase = "cerberus";

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);

    public string Where =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? $"{uri.Host}:{(uri.Port > 0 ? uri.Port : 5432)}/{Database(uri)}" : "an address that cannot be read";

    private static string Database(Uri uri) =>
        Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')) is { Length: > 0 } name ? name : DefaultDatabase;

    /// <summary>Why the address cannot be used, or null. Checked at start-up with the other settings.</summary>
    public static string? Problem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("postgres" or "postgresql") || uri.Host.Length == 0)
            return $"{Variable} must be a postgres:// address: postgres://user:password@host:5432/cerberus.";
        if (!System.Text.RegularExpressions.Regex.IsMatch(Database(uri), "^[A-Za-z0-9_-]{1,63}$"))
            return $"{Variable} must name a database of letters, digits, '_' or '-' (or none, for \"{DefaultDatabase}\").";
        var mode = System.Web.HttpUtility.ParseQueryString(uri.Query)["sslmode"];
        if (mode is not null && !Enum.TryParse<SslMode>(mode.Replace("-", ""), ignoreCase: true, out _))
            return $"{Variable}: sslmode must be one of disable, allow, prefer, require, verify-ca, verify-full.";
        return null;
    }

    public string ConnectionString()
    {
        var uri = new Uri(Url ?? throw new InvalidOperationException("No PostgreSQL address is configured."));
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Database(uri),
            ApplicationName = "cerberus",
            Timeout = (int)BackingServices.AttemptLimit.TotalSeconds,
        };
        var credentials = uri.UserInfo.Split(':', 2);
        if (credentials[0].Length > 0) settings.Username = Uri.UnescapeDataString(credentials[0]);
        if (credentials.Length == 2) settings.Password = Uri.UnescapeDataString(credentials[1]);
        if (System.Web.HttpUtility.ParseQueryString(uri.Query)["sslmode"] is { } mode)
            settings.SslMode = Enum.Parse<SslMode>(mode.Replace("-", ""), ignoreCase: true);
        return settings.ConnectionString;
    }

    /// <summary>The database context settings for this server, as the API and the tests use them.</summary>
    public void Configure(DbContextOptionsBuilder options) =>
        options.UseNpgsql(ConnectionString());
}

/// <summary>PostgreSQL answers, and the database exists — it is created if the server has none by that name.</summary>
public sealed class PostgresProbe(PostgresOptions options) : IServiceProbe
{
    public string Name => "PostgreSQL";
    public string Variable => PostgresOptions.Variable;
    public bool Configured => options.Enabled;
    public string Where => options.Where;
    public string StandIn => "the local SQLite file";

    public async Task ProbeAsync(CancellationToken ct)
    {
        var settings = new NpgsqlConnectionStringBuilder(options.ConnectionString()) { Pooling = false };
        try
        {
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "3D000")
        {
            // The server is there and the user is let in, but has no database by that name yet: make it.
            var database = settings.Database!;
            settings.Database = "postgres";
            await using var server = new NpgsqlConnection(settings.ConnectionString);
            await server.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", server);
            try
            {
                await create.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException again) when (again.SqlState == "42P04")
            {
                // Another copy of the service made it a moment ago.
            }
        }
    }

    /// <summary>Makes or upgrades the schema there: the last step before the database is chosen.</summary>
    public void PrepareSchema()
    {
        var builder = new DbContextOptionsBuilder<CerberusDbContext>();
        options.Configure(builder);
        using var db = new CerberusDbContext(builder.Options);
        SchemaUpgrader.EnsureUpToDate(db);
    }
}

/// <summary>Redis answers a PING.</summary>
public sealed class RedisProbe(RedisOptions options) : IServiceProbe
{
    public string Name => "Redis";
    public string Variable => "CERBERUS_REDIS_URL";
    public bool Configured => options.Enabled;
    public string Where => options.Where;
    public string StandIn => "this process alone (signals, places in line, sign-in and request counts)";

    public async Task ProbeAsync(CancellationToken ct)
    {
        var settings = options.Configuration();
        // A plain connection first: the Redis client waits out its whole timeout on a port where nothing
        // listens, where the network says so at once.
        var address = new Uri(options.Url!);
        using (var socket = new System.Net.Sockets.TcpClient()) await socket.ConnectAsync(address.Host, address.Port > 0 ? address.Port : 6379, ct);
        settings.AbortOnConnectFail = true;
        settings.ConnectRetry = 1;
        settings.ConnectTimeout = (int)BackingServices.AttemptLimit.TotalMilliseconds;
        settings.ClientName = "cerberus-probe";
        await using var connection = await ConnectionMultiplexer.ConnectAsync(settings).WaitAsync(ct);
        await connection.GetDatabase().PingAsync().WaitAsync(ct);
    }
}

/// <summary>RabbitMQ accepts a connection with the configured user.</summary>
public sealed class RabbitMqProbe(RabbitMqOptions options) : IServiceProbe
{
    public string Name => "RabbitMQ";
    public string Variable => "CERBERUS_RABBITMQ_URL";
    public bool Configured => options.Enabled;
    public string Where => options.Where;
    public string StandIn => "the queue inside this process";

    public async Task ProbeAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Url!),
            AutomaticRecoveryEnabled = false,
            RequestedConnectionTimeout = BackingServices.AttemptLimit,
            ClientProvidedName = "cerberus-probe",
        };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await connection.CloseAsync(ct);
    }
}

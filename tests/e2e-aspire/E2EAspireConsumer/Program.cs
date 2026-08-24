using System.Globalization;
using Npgsql;
using StackExchange.Redis;

// The Aspire "service": it receives ConnectionStrings__op3cache / ConnectionStrings__op3pg from the
// AppHost (which resolves them to the *host* side of the published ports), actually talks to both
// containers, and then prints ASPIRE_OK. Nothing here knows about cider: if the daemon's
// published-port proxy or container start path is broken, this process fails.

var sentinelPath = Environment.GetEnvironmentVariable("ASPIRE_E2E_SENTINEL");
var lines = new List<string>();

void Report(string line)
{
    lines.Add(line);
    Console.WriteLine(line);
    Console.Out.Flush();
}

try
{
    var redisConnectionString = Require("ConnectionStrings__op3cache");
    var postgresConnectionString = Require("ConnectionStrings__op3pg");
    Report("CONSUMER_REDIS_ENDPOINT=" + Redact(redisConnectionString));
    Report("CONSUMER_POSTGRES_ENDPOINT=" + Redact(postgresConnectionString));

    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

    var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
    await using (redis)
    {
        var database = redis.GetDatabase();
        var token = "op3-" + Guid.NewGuid().ToString("n")[..8];
        await database.StringSetAsync("op3:probe", token);
        var roundTripped = (string?)await database.StringGetAsync("op3:probe");
        if (!string.Equals(roundTripped, token, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"redis round trip lost the value: wrote {token}, read {roundTripped ?? "(nil)"}");
        }

        Report("CONSUMER_REDIS_ROUNDTRIP=" + roundTripped);
    }

    await using var connection = new NpgsqlConnection(postgresConnectionString);
    await connection.OpenAsync(timeout.Token);
    await using (var command = new NpgsqlCommand("select 40 + 2", connection))
    {
        var answer = await command.ExecuteScalarAsync(timeout.Token);
        var text = Convert.ToString(answer, CultureInfo.InvariantCulture);
        if (!string.Equals(text, "42", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("postgres returned " + (text ?? "(null)") + " instead of 42");
        }

        Report("CONSUMER_POSTGRES_QUERY=" + text);
    }

    Report("ASPIRE_OK");
    Write(sentinelPath, lines);
    return 0;
}
catch (Exception ex)
{
    Report("ASPIRE_CONSUMER_FAILED");
    Report(ex.ToString());
    Write(sentinelPath, lines);
    return 1;
}

static string Require(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"the AppHost did not inject {name}");

// Postgres connection strings carry the generated password; keep it out of the test log.
static string Redact(string connectionString) =>
    string.Join(
        ';',
        connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("password=", StringComparison.OrdinalIgnoreCase)));

// DCP owns the child's console, so the AppHost reads the outcome back through a file it named.
static void Write(string? path, IEnumerable<string> lines)
{
    if (string.IsNullOrEmpty(path))
    {
        return;
    }

    try
    {
        File.WriteAllLines(path, lines);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
    }
}

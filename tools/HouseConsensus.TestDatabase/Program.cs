using System.Text.RegularExpressions;
using Npgsql;

if (args.Length != 1 || args[0] is not ("create" or "drop"))
    throw new InvalidOperationException("Expected create or drop.");
var adminUrl = Environment.GetEnvironmentVariable("TEST_DATABASE_ADMIN_URL")
    ?? throw new InvalidOperationException("TEST_DATABASE_ADMIN_URL is required.");
var databaseName = Environment.GetEnvironmentVariable("TEST_DATABASE_NAME")
    ?? throw new InvalidOperationException("TEST_DATABASE_NAME is required.");
if (!Regex.IsMatch(databaseName, "^house_consensus_test_e2e_[a-z0-9_]+$", RegexOptions.CultureInvariant))
    throw new InvalidOperationException("Refusing unsafe test database name.");

var builder = new NpgsqlConnectionStringBuilder(adminUrl);
await using var connection = new NpgsqlConnection(builder.ConnectionString);
await connection.OpenAsync();
await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)", connection);
exists.Parameters.AddWithValue("name", databaseName);
var present = (bool)(await exists.ExecuteScalarAsync() ?? false);
var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
if (args[0] == "create" && !present)
    await new NpgsqlCommand($"CREATE DATABASE {quoted}", connection).ExecuteNonQueryAsync();
if (args[0] == "drop" && present)
    await new NpgsqlCommand($"DROP DATABASE {quoted} WITH (FORCE)", connection).ExecuteNonQueryAsync();

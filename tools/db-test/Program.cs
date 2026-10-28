using Npgsql;

const string host = "aws-0-ap-northeast-1.pooler.supabase.com";
const string user = "postgres.prxmkrmwqxlltwjnazay";
const string pass = "YOUR_DB_PASSWORD";

var cs = new NpgsqlConnectionStringBuilder
{
    Host = host,
    Port = 6543,
    Username = user,
    Password = pass,
    Database = "postgres",
    SslMode = SslMode.Require,
    Timeout = 15,
}.ConnectionString;

Console.WriteLine($"user={user} host={host} passLen={pass.Length}");
try
{
    await using var conn = new NpgsqlConnection(cs);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT current_user", conn);
    var u = await cmd.ExecuteScalarAsync();
    Console.WriteLine($"SUCCESS connected as {u}");
}
catch (Exception ex)
{
    Console.WriteLine($"FAIL: {ex.Message.Split('\n')[0]}");
}

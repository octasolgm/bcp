using Npgsql;

const string host = "aws-1-ap-northeast-2.pooler.supabase.com";
const string user = "postgres.hxfbzhjlmkiqhbbeftfq";
const string pass = "23Gmrehman@123";

var cs = new NpgsqlConnectionStringBuilder
{
    Host = host,
    Port = 5432,
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

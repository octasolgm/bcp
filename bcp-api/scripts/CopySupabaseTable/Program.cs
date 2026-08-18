using Npgsql;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var mode = args[0].ToLowerInvariant();
var truncate = args.Contains("--truncate", StringComparer.OrdinalIgnoreCase);

return mode switch
{
    "import-csv" => await ImportCsvAsync(args, truncate),
    "copy" => await CopyTableAsync(args, truncate),
    "export-storage-paths" => await ExportStoragePathsAsync(args),
    _ when args.Length >= 3 && IsSafeIdentifier(args[0]) => await CopyTableAsync(["copy", ..args], truncate),
    _ => PrintUsage(),
};

static int PrintUsage()
{
    Console.WriteLine("""
Usage:
  # Live old DB -> new DB (old project must be online)
  dotnet run --project scripts/CopySupabaseTable -- copy <table> <sourceConn> <destConn> [--truncate]

  # Local CSV file -> new DB (full data incl. JSON — no Supabase UI)
  dotnet run --project scripts/CopySupabaseTable -- import-csv <table> <csvPath> <destConn> [--truncate]
  dotnet run --project scripts/CopySupabaseTable -- export-storage-paths <destConn> [outputFile]

PowerShell wrappers:
  .\scripts\copy-supabase-table.ps1 -Table analysis_runs
  .\scripts\import-csv-table.ps1 -Table analysis_runs -CsvPath "C:\path\analysis_runs_rows.csv"
  .\scripts\copy-supabase-storage.ps1 -FromDatabase
""");
    return 1;
}

static async Task<int> ExportStoragePathsAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("export-storage-paths requires: <destConn> [outputFile]");
        return 1;
    }

    var destCs = await ResolveDestinationAsync(args[1]);
    var outputFile = args.Length >= 3 ? args[2] : null;

    const string sql = """
        SELECT DISTINCT path FROM (
          SELECT storage_path AS path FROM stored_documents
          WHERE storage_path IS NOT NULL AND storage_path <> ''
          UNION
          SELECT source_storage_path AS path FROM stored_documents
          WHERE source_storage_path IS NOT NULL AND source_storage_path <> ''
        ) x
        ORDER BY path
        """;

    await using var conn = new NpgsqlConnection(destCs);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    await using var reader = await cmd.ExecuteReaderAsync();

    var paths = new List<string>();
    while (await reader.ReadAsync())
        paths.Add(reader.GetString(0));

    if (outputFile is not null)
    {
        await File.WriteAllLinesAsync(outputFile, paths);
        Console.WriteLine($"Wrote {paths.Count} path(s) to {outputFile}");
    }
    else
    {
        foreach (var path in paths)
            Console.WriteLine(path);
    }

    return 0;
}

static async Task<int> ImportCsvAsync(string[] args, bool truncate)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("import-csv requires: <table> <csvPath> <destConn>");
        return 1;
    }

    var table = args[1];
    var csvPath = args[2];
    var destRaw = args[3];

    if (!IsSafeIdentifier(table))
    {
        Console.Error.WriteLine($"Invalid table name: {table}");
        return 1;
    }

    if (!File.Exists(csvPath))
    {
        Console.Error.WriteLine($"CSV not found: {csvPath}");
        return 1;
    }

    var fileInfo = new FileInfo(csvPath);
    Console.WriteLine($"CSV: {csvPath} ({fileInfo.Length / 1024} KB)");

    var destCs = await ResolveDestinationAsync(destRaw);
    await using var dst = new NpgsqlConnection(destCs);
    await dst.OpenAsync();

    if (truncate)
    {
        Console.WriteLine($"Truncating public.{table} ...");
        await using var trunc = new NpgsqlCommand($"TRUNCATE TABLE public.{table} CASCADE", dst);
        await trunc.ExecuteNonQueryAsync();
    }

    Console.WriteLine($"Importing into public.{table} via COPY ...");
    switch (table)
    {
        case "landing_ai_extract_cache":
            await ImportLandingAiExtractCacheFromCsvAsync(dst, csvPath);
            break;
        case "landing_ai_parse_cache":
            await ImportLandingAiParseCacheFromCsvAsync(dst, csvPath);
            break;
        default:
        {
            var columns = GetCsvCopyColumns(table);
            var copyIn = string.IsNullOrEmpty(columns)
                ? $"COPY public.{table} FROM STDIN WITH (FORMAT csv, HEADER true, QUOTE '\"', ESCAPE '\"')"
                : $"COPY public.{table} ({columns}) FROM STDIN WITH (FORMAT csv, HEADER true, QUOTE '\"', ESCAPE '\"')";
            await using var import = await dst.BeginTextImportAsync(copyIn);
            using var reader = new StreamReader(csvPath);
            var buffer = new char[64 * 1024];
            int read;
            long chars = 0;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await import.WriteAsync(buffer.AsMemory(0, read));
                chars += read;
            }

            await import.DisposeAsync();
            Console.WriteLine($"Done. Sent ~{chars / 1024} KB to public.{table}.");
            break;
        }
    }

    await using var verify = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{table}", dst);
    var n = (long)(await verify.ExecuteScalarAsync() ?? 0L);
    Console.WriteLine($"Destination {table}: {n} row(s)");
    return 0;
}

static async Task<int> CopyTableAsync(string[] args, bool truncate)
{
    var offset = string.Equals(args[0], "copy", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    if (args.Length < offset + 3)
    {
        Console.Error.WriteLine("copy requires: <table> <sourceConn> <destConn>");
        return 1;
    }

    var table = args[offset];
    if (!IsSafeIdentifier(table))
    {
        Console.Error.WriteLine($"Invalid table name: {table}");
        return 1;
    }

    var sourceCs = await ResolveSourceAsync(args[offset + 1]);
    var destCs = await ResolveDestinationAsync(args[offset + 2]);

    Console.WriteLine("Connecting to source...");
    await using var src = new NpgsqlConnection(sourceCs);
    await src.OpenAsync();

    Console.WriteLine("Connecting to destination...");
    await using var dst = new NpgsqlConnection(destCs);
    await dst.OpenAsync();

    var countSql = $"SELECT COUNT(*) FROM public.{table}";
    await using (var countCmd = new NpgsqlCommand(countSql, src))
    {
        var n = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);
        Console.WriteLine($"Source {table}: {n} row(s)");
    }

    if (truncate)
    {
        Console.WriteLine($"Truncating destination public.{table} ...");
        await using var trunc = new NpgsqlCommand($"TRUNCATE TABLE public.{table} CASCADE", dst);
        await trunc.ExecuteNonQueryAsync();
    }

    Console.WriteLine($"Copying public.{table} ...");
    var copyOut = $"COPY public.{table} TO STDOUT WITH (FORMAT csv, HEADER true, QUOTE '\"', ESCAPE '\"')";
    var copyIn = $"COPY public.{table} FROM STDIN WITH (FORMAT csv, HEADER true, QUOTE '\"', ESCAPE '\"')";

    using var export = await src.BeginTextExportAsync(copyOut);
    var import = await dst.BeginTextImportAsync(copyIn);

    var buffer = new char[64 * 1024];
    int read;
    long chars = 0;
    try
    {
        while ((read = await export.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await import.WriteAsync(buffer.AsMemory(0, read));
            chars += read;
        }
    }
    finally
    {
        import.Dispose();
    }

    Console.WriteLine($"Done. Copied ~{chars / 1024} KB into public.{table}.");

    await using (var verify = new NpgsqlCommand(countSql, dst))
    {
        var n = (long)(await verify.ExecuteScalarAsync() ?? 0L);
        Console.WriteLine($"Destination {table}: {n} row(s)");
    }

    return 0;
}

static string ResolveConnectionString(string raw, string label)
{
    if (string.IsNullOrWhiteSpace(raw))
        throw new ArgumentException($"Missing {label} connection string.");

    raw = raw.Trim();

    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
        || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return ParsePostgresUri(raw);
    }

    _ = new NpgsqlConnectionStringBuilder(raw);
    return raw;
}

static string ParsePostgresUri(string uri)
{
    if (!Uri.TryCreate(uri, UriKind.Absolute, out var u))
        throw new ArgumentException($"Invalid postgres URI: {uri}");

    var userInfo = u.UserInfo;
    var colon = userInfo.IndexOf(':');
    var username = colon >= 0
        ? Uri.UnescapeDataString(userInfo[..colon])
        : Uri.UnescapeDataString(userInfo);
    var password = colon >= 0
        ? Uri.UnescapeDataString(userInfo[(colon + 1)..])
        : "";

    var database = u.AbsolutePath.TrimStart('/');
    if (string.IsNullOrEmpty(database)) database = "postgres";

    return new NpgsqlConnectionStringBuilder
    {
        Host = u.Host,
        Port = u.Port > 0 ? u.Port : 5432,
        Database = database,
        Username = username,
        Password = password,
        SslMode = SslMode.Require,
        Timeout = 30,
        CommandTimeout = 0,
    }.ConnectionString;
}

static async Task<string> ResolveDestinationAsync(string raw)
{
    var cs = ResolveConnectionString(raw, "destination");
    if (await CanConnectAsync(cs))
        return cs;

    var builder = new NpgsqlConnectionStringBuilder(cs);
    var projectRef = TryGetProjectRef(builder);
    if (string.IsNullOrWhiteSpace(projectRef))
        throw new InvalidOperationException("Could not connect to destination database.");

    Console.WriteLine($"Direct connection failed — probing pooler for {projectRef} ...");
    var pooler = await FindPoolerAsync(projectRef, builder.Password ?? "");
    if (pooler is not null)
    {
        Console.WriteLine($"Destination pooler: {pooler.Host}");
        return pooler.ConnectionString;
    }

    throw new InvalidOperationException("Could not connect to destination database. Check password in appsettings.Secrets.json.");
}

static async Task<string> ResolveSourceAsync(string raw)
{
    var cs = ResolveConnectionString(raw, "source");
    if (await CanConnectAsync(cs))
        return cs;

    var builder = new NpgsqlConnectionStringBuilder(cs);
    var projectRef = TryGetProjectRef(builder);
    if (string.IsNullOrWhiteSpace(projectRef))
        throw new InvalidOperationException("Could not connect to source database.");

    Console.WriteLine($"Source failed — probing pooler for {projectRef} ...");
    var pooler = await FindPoolerAsync(projectRef, builder.Password ?? "");
    if (pooler is not null)
    {
        Console.WriteLine($"Source pooler: {pooler.Host}");
        return pooler.ConnectionString;
    }

    throw new InvalidOperationException(
        "Could not connect to OLD database. Project may be quota-blocked — use import-csv-table.ps1 with your local CSV instead.");
}

static string? TryGetProjectRef(NpgsqlConnectionStringBuilder builder)
{
    var user = builder.Username ?? "";
    if (user.StartsWith("postgres.", StringComparison.OrdinalIgnoreCase))
        return user["postgres.".Length..];

    var host = builder.Host ?? "";
    const string prefix = "db.";
    const string suffix = ".supabase.co";
    if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
    {
        return host[prefix.Length..^suffix.Length];
    }

    return null;
}

static async Task<NpgsqlConnectionStringBuilder?> FindPoolerAsync(string projectRef, string password)
{
    foreach (var prefix in new[] { "aws-0", "aws-1", "aws-2" })
    foreach (var region in new[]
             {
                 "ap-southeast-1", "ap-south-1", "ap-northeast-1", "ap-northeast-2",
                 "eu-west-1", "eu-central-1", "us-east-1", "us-east-2", "us-west-1",
             })
    {
        var probe = new NpgsqlConnectionStringBuilder
        {
            Host = $"{prefix}-{region}.pooler.supabase.com",
            Port = 5432,
            Database = "postgres",
            Username = $"postgres.{projectRef}",
            Password = password,
            SslMode = SslMode.Require,
            Timeout = 8,
            CommandTimeout = 0,
        };

        if (await CanConnectAsync(probe.ConnectionString))
            return probe;
    }

    return null;
}

static async Task<bool> CanConnectAsync(string connectionString)
{
    try
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return true;
    }
    catch
    {
        return false;
    }
}

/// <summary>
/// Old Supabase CSV: id,file_hash,schema_key,points_json,extract_model,credit_usage,created_at
/// New table PK: (file_hash, schema_key) — no id column. Stage full CSV then insert mapped columns.
/// </summary>
static async Task ImportLandingAiExtractCacheFromCsvAsync(NpgsqlConnection dst, string csvPath)
{
    const string staging = "_import_landing_ai_extract_cache";

    await using (var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {staging}", dst))
        await drop.ExecuteNonQueryAsync();

    await using (var create = new NpgsqlCommand($"""
        CREATE TEMP TABLE {staging} (
          id UUID,
          file_hash TEXT,
          schema_key TEXT,
          points_json JSONB,
          extract_model TEXT,
          credit_usage JSONB,
          created_at TIMESTAMPTZ
        )
        """, dst))
        await create.ExecuteNonQueryAsync();

    var copyIn = $"COPY {staging} FROM STDIN WITH (FORMAT csv, HEADER true, QUOTE '\"', ESCAPE '\"')";
    await using (var import = await dst.BeginTextImportAsync(copyIn))
    {
        using var reader = new StreamReader(csvPath);
        var buffer = new char[64 * 1024];
        int read;
        long chars = 0;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await import.WriteAsync(buffer.AsMemory(0, read));
            chars += read;
        }

        Console.WriteLine($"Done. Staged ~{chars / 1024} KB from CSV.");
    }

    await using (var merge = new NpgsqlCommand($"""
        INSERT INTO public.landing_ai_extract_cache (file_hash, schema_key, points_json, extract_model, updated_at)
        SELECT file_hash, schema_key, points_json, extract_model, COALESCE(created_at, now())
        FROM {staging}
        WHERE file_hash IS NOT NULL AND schema_key IS NOT NULL
        ON CONFLICT (file_hash, schema_key) DO UPDATE SET
          points_json = EXCLUDED.points_json,
          extract_model = EXCLUDED.extract_model,
          updated_at = EXCLUDED.updated_at
        """, dst))
    {
        var rows = await merge.ExecuteNonQueryAsync();
        Console.WriteLine($"Merged {rows} row(s) into public.landing_ai_extract_cache.");
    }
}

/// <summary>
/// Old Supabase CSV: id,file_hash,file_name,markdown,chunks_json,parse_model,credit_usage,created_at,updated_at
/// New table PK: file_hash — no id/chunks_json/credit_usage columns.
/// </summary>
static async Task ImportLandingAiParseCacheFromCsvAsync(NpgsqlConnection dst, string csvPath)
{
    const string staging = "_import_landing_ai_parse_cache";

    await using (var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {staging}", dst))
        await drop.ExecuteNonQueryAsync();

    await using (var create = new NpgsqlCommand($"""
        CREATE TEMP TABLE {staging} (
          id UUID,
          file_hash TEXT,
          file_name TEXT,
          markdown TEXT,
          chunks_json JSONB,
          parse_model TEXT,
          credit_usage JSONB,
          created_at TIMESTAMPTZ,
          updated_at TIMESTAMPTZ
        )
        """, dst))
        await create.ExecuteNonQueryAsync();

    var copyIn = $"COPY {staging} FROM STDIN WITH (FORMAT csv, HEADER true, QUOTE '\"', ESCAPE '\"')";
    await using (var import = await dst.BeginTextImportAsync(copyIn))
    {
        using var reader = new StreamReader(csvPath);
        var buffer = new char[64 * 1024];
        int read;
        long chars = 0;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await import.WriteAsync(buffer.AsMemory(0, read));
            chars += read;
        }

        Console.WriteLine($"Done. Staged ~{chars / 1024} KB from CSV.");
    }

    await using (var merge = new NpgsqlCommand($"""
        INSERT INTO public.landing_ai_parse_cache (file_hash, file_name, markdown, parse_model, updated_at)
        SELECT file_hash, file_name, markdown, parse_model, COALESCE(updated_at, created_at, now())
        FROM {staging}
        WHERE file_hash IS NOT NULL
        ON CONFLICT (file_hash) DO UPDATE SET
          file_name = EXCLUDED.file_name,
          markdown = EXCLUDED.markdown,
          parse_model = EXCLUDED.parse_model,
          updated_at = EXCLUDED.updated_at
        """, dst))
    {
        var rows = await merge.ExecuteNonQueryAsync();
        Console.WriteLine($"Merged {rows} row(s) into public.landing_ai_parse_cache.");
    }
}

/// <summary>When CSV columns differ from table defaults, list COPY target columns (must exist in DB).</summary>
static string? GetCsvCopyColumns(string table) => table switch
{
    _ => null,
};

static bool IsSafeIdentifier(string name) =>
    !string.IsNullOrWhiteSpace(name)
    && name.All(c => char.IsLetterOrDigit(c) || c == '_');

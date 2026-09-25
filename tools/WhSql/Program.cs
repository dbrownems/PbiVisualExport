using Microsoft.Data.SqlClient;

// Usage: WhSql <server> <database> <sqlFile>
// Auth: reads bearer token for https://database.windows.net/ from env SQLTOKEN.
// Batches are split on lines containing only "GO".

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: WhSql <server> <database> <sqlFile>");
    return 2;
}

var server = args[0];
var database = args[1];
var sqlFile = args[2];
var token = Environment.GetEnvironmentVariable("SQLTOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("SQLTOKEN env var (access token for https://database.windows.net/) is required.");
    return 2;
}

var csb = new SqlConnectionStringBuilder
{
    DataSource = $"tcp:{server},1433",
    InitialCatalog = database,
    Encrypt = true,
    TrustServerCertificate = false,
    ConnectTimeout = 60
};

var script = await File.ReadAllTextAsync(sqlFile);
var batches = SplitBatches(script);

await using var conn = new SqlConnection(csb.ConnectionString) { AccessToken = token };
await conn.OpenAsync();
Console.WriteLine($"Connected to {server} / {database}. Running {batches.Count} batch(es).");

int n = 0;
foreach (var batch in batches)
{
    if (string.IsNullOrWhiteSpace(batch)) continue;
    n++;
    await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 300 };
    try
    {
        // If the batch returns rows, print them; otherwise print rows-affected.
        await using var reader = await cmd.ExecuteReaderAsync();
        if (reader.FieldCount > 0 && reader.HasRows)
        {
            var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            Console.WriteLine("  " + string.Join(" | ", cols));
            while (await reader.ReadAsync())
            {
                var vals = Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString());
                Console.WriteLine("  " + string.Join(" | ", vals));
            }
        }
        else
        {
            Console.WriteLine($"  batch {n}: {reader.RecordsAffected} row(s) affected.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  batch {n} FAILED: {ex.Message}");
        return 1;
    }
}
Console.WriteLine("Done.");
return 0;

static List<string> SplitBatches(string script)
{
    var result = new List<string>();
    var sb = new System.Text.StringBuilder();
    foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
    {
        if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(sb.ToString());
            sb.Clear();
        }
        else sb.AppendLine(line);
    }
    if (sb.Length > 0) result.Add(sb.ToString());
    return result;
}

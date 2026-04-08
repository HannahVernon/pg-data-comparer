using System.CommandLine;
using System.Data;
using System.Text;
using Npgsql;

namespace PgDataComparer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var sourceHostOption     = new Option<string>("--source-host",      "Source PostgreSQL host")                    { IsRequired = true };
        var sourcePortOption     = new Option<int>(   "--source-port",      () => 5432, "Source PostgreSQL port");
        var sourceDatabaseOption = new Option<string>("--source-database",  "Source database name")                     { IsRequired = true };
        var sourceSchemaOption   = new Option<string>("--source-schema",    () => "public", "Source schema name");
        var sourceTableOption    = new Option<string>("--source-table",     "Source table name")                        { IsRequired = true };
        var sourceUserOption     = new Option<string>("--source-user",      "Source database username")                 { IsRequired = true };
        var sourcePasswordOption = new Option<string?>("--source-password",  "Source database password (prompts securely if omitted)");

        var targetHostOption     = new Option<string>("--target-host",      "Target PostgreSQL host")                   { IsRequired = true };
        var targetPortOption     = new Option<int>(   "--target-port",      () => 5432, "Target PostgreSQL port");
        var targetDatabaseOption = new Option<string>("--target-database",  "Target database name")                     { IsRequired = true };
        var targetSchemaOption   = new Option<string>("--target-schema",    () => "public", "Target schema name");
        var targetTableOption    = new Option<string>("--target-table",     "Target table name")                        { IsRequired = true };
        var targetUserOption     = new Option<string>("--target-user",      "Target database username")                 { IsRequired = true };
        var targetPasswordOption = new Option<string?>("--target-password",  "Target database password (prompts securely if omitted)");

        var whereOption            = new Option<string?>("--where",              "WHERE clause applied to both queries (without the WHERE keyword)");
        var sourceWhereOption      = new Option<string?>("--source-where",       "WHERE clause for source query only (overrides --where for source)");
        var targetWhereOption      = new Option<string?>("--target-where",       "WHERE clause for target query only (overrides --where for target)");
        var outputDirOption         = new Option<string>("--output-dir",         () => ".", "Directory for output TSV files");
        var sortByOption            = new Option<string?>("--sort-by",           "Comma-separated column list for ORDER BY (auto-detects primary key if omitted)");
        var nullTextOption          = new Option<string>("--null-text",          () => "<NULL>", "Text representation for NULL values in TSV output");
        var excludeGeneratedOption  = new Option<bool>("--exclude-generated",   () => false, "Exclude identity, serial, and sequence-default columns from output");
        var includeSystemOption     = new Option<bool>("--include-postgres-system-objects", () => false, "Allow comparing tables in PostgreSQL system schemas (pg_catalog, pg_toast, information_schema, pg_temp)");

        var rootCommand = new RootCommand("Compare table content between two PostgreSQL databases and export to TSV files for diff tools.")
        {
            sourceHostOption, sourcePortOption, sourceDatabaseOption, sourceSchemaOption, sourceTableOption, sourceUserOption, sourcePasswordOption,
            targetHostOption, targetPortOption, targetDatabaseOption, targetSchemaOption, targetTableOption, targetUserOption, targetPasswordOption,
            whereOption, sourceWhereOption, targetWhereOption, outputDirOption, sortByOption, nullTextOption, excludeGeneratedOption, includeSystemOption
        };

        rootCommand.SetHandler(async (context) =>
        {
            var sourceHost     = context.ParseResult.GetValueForOption(sourceHostOption)!;
            var sourceUser     = context.ParseResult.GetValueForOption(sourceUserOption)!;
            var sourcePassword = context.ParseResult.GetValueForOption(sourcePasswordOption)
                                 ?? ReadPasswordFromConsole($"Password for {sourceUser}@{sourceHost} (source): ");

            var targetHost     = context.ParseResult.GetValueForOption(targetHostOption)!;
            var targetUser     = context.ParseResult.GetValueForOption(targetUserOption)!;
            var targetPassword = context.ParseResult.GetValueForOption(targetPasswordOption)
                                 ?? ReadPasswordFromConsole($"Password for {targetUser}@{targetHost} (target): ");

            var sourceConfig = new TableConfig(
                sourceHost,
                context.ParseResult.GetValueForOption(sourcePortOption),
                context.ParseResult.GetValueForOption(sourceDatabaseOption)!,
                context.ParseResult.GetValueForOption(sourceSchemaOption)!,
                context.ParseResult.GetValueForOption(sourceTableOption)!,
                sourceUser,
                sourcePassword
            );

            var targetConfig = new TableConfig(
                targetHost,
                context.ParseResult.GetValueForOption(targetPortOption),
                context.ParseResult.GetValueForOption(targetDatabaseOption)!,
                context.ParseResult.GetValueForOption(targetSchemaOption)!,
                context.ParseResult.GetValueForOption(targetTableOption)!,
                targetUser,
                targetPassword
            );

            var sharedWhere      = context.ParseResult.GetValueForOption(whereOption);
            var sourceWhere      = context.ParseResult.GetValueForOption(sourceWhereOption) ?? sharedWhere;
            var targetWhere      = context.ParseResult.GetValueForOption(targetWhereOption) ?? sharedWhere;
            var outputDir        = context.ParseResult.GetValueForOption(outputDirOption)!;
            var sortBy           = context.ParseResult.GetValueForOption(sortByOption);
            var nullText         = context.ParseResult.GetValueForOption(nullTextOption)!;
            var excludeGenerated = context.ParseResult.GetValueForOption(excludeGeneratedOption);
            var includeSystem    = context.ParseResult.GetValueForOption(includeSystemOption);

            if (!includeSystem)
            {
                var systemSchemas = new[] { sourceConfig.Schema, targetConfig.Schema }
                    .Where(IsSystemSchema)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (systemSchemas.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"Error: refusing to compare tables in PostgreSQL system schema(s): {string.Join(", ", systemSchemas)}");
                    Console.Error.WriteLine("Use --include-postgres-system-objects to override this check.");
                    Console.ResetColor();
                    context.ExitCode = 1;
                    return;
                }
            }

            context.ExitCode = await RunCompareAsync(sourceConfig, targetConfig, sourceWhere, targetWhere, outputDir, sortBy, nullText, excludeGenerated);
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<int> RunCompareAsync(
        TableConfig source, TableConfig target,
        string? sourceWhere, string? targetWhere,
        string outputDir, string? sortBy, string nullText,
        bool excludeGenerated)
    {
        try
        {
            Directory.CreateDirectory(outputDir);

            var sourceLabel = $"{source.Host}_{source.Database}_{source.Schema}_{source.Table}";
            var targetLabel = $"{target.Host}_{target.Database}_{target.Schema}_{target.Table}";

            var sourceFile = Path.Combine(outputDir, SanitizeFileName(sourceLabel) + ".tsv");
            var targetFile = Path.Combine(outputDir, SanitizeFileName(targetLabel) + ".tsv");

            /* If both files would have the same name, disambiguate */
            if (string.Equals(sourceFile, targetFile, StringComparison.OrdinalIgnoreCase))
            {
                sourceFile = Path.Combine(outputDir, SanitizeFileName(sourceLabel) + "_source.tsv");
                targetFile = Path.Combine(outputDir, SanitizeFileName(targetLabel) + "_target.tsv");
            }

            Console.WriteLine($"Source: {source.Host}:{source.Port}/{source.Database} -> {source.Schema}.{source.Table}");
            Console.WriteLine($"Target: {target.Host}:{target.Port}/{target.Database} -> {target.Schema}.{target.Table}");
            if (!string.IsNullOrWhiteSpace(sourceWhere))
                Console.WriteLine($"Source WHERE: {sourceWhere}");
            if (!string.IsNullOrWhiteSpace(targetWhere))
                Console.WriteLine($"Target WHERE: {targetWhere}");
            if (excludeGenerated)
                Console.WriteLine("Excluding auto-generated / sequence columns.");
            Console.WriteLine();

            var sourceTask = ExportTableAsync(source, sourceWhere, sortBy, nullText, excludeGenerated, sourceFile, "Source");
            var targetTask = ExportTableAsync(target, targetWhere, sortBy, nullText, excludeGenerated, targetFile, "Target");

            await Task.WhenAll(sourceTask, targetTask);

            Console.WriteLine();
            Console.WriteLine($"Source TSV: {Path.GetFullPath(sourceFile)}");
            Console.WriteLine($"Target TSV: {Path.GetFullPath(targetFile)}");
            Console.WriteLine("Done. Open both files in a diff tool (e.g. BeyondCompare) to compare.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task ExportTableAsync(
        TableConfig config, string? whereClause, string? sortBy,
        string nullText, bool excludeGenerated, string outputPath, string label)
    {
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host     = config.Host,
            Port     = config.Port,
            Database = config.Database,
            Username = config.User,
            Password = config.Password,
            Timeout  = 30,
            CommandTimeout = 0
        }.ConnectionString;

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var orderBy = sortBy;
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            orderBy = await DetectPrimaryKeyColumnsAsync(conn, config.Schema, config.Table);
            if (!string.IsNullOrWhiteSpace(orderBy))
                Console.WriteLine($"[{label}] Auto-detected ORDER BY: {orderBy}");
            else
                Console.WriteLine($"[{label}] Warning: no primary key found; output order is non-deterministic.");
        }

        var quotedTable = $"\"{config.Schema}\".\"{config.Table}\"";

        /* Determine column list */
        var columnList = "*";
        if (excludeGenerated)
        {
            var excluded = await DetectGeneratedColumnsAsync(conn, config.Schema, config.Table);
            if (excluded.Count > 0)
            {
                Console.WriteLine($"[{label}] Excluding columns: {string.Join(", ", excluded)}");
                var allColumns = await GetAllColumnNamesAsync(conn, config.Schema, config.Table);
                var included = allColumns
                    .Where(c => !excluded.Contains(c, StringComparer.OrdinalIgnoreCase))
                    .Select(c => $"\"{c}\"")
                    .ToList();

                if (included.Count == 0)
                {
                    Console.WriteLine($"[{label}] Warning: all columns are generated; falling back to SELECT *.");
                }
                else
                {
                    columnList = string.Join(", ", included);
                }
            }
        }

        var sql = new StringBuilder();
        sql.Append($"SELECT {columnList} FROM {quotedTable}");

        if (!string.IsNullOrWhiteSpace(whereClause))
            sql.Append($" WHERE {whereClause}");

        if (!string.IsNullOrWhiteSpace(orderBy))
            sql.Append($" ORDER BY {orderBy}");

        await using var cmd    = new NpgsqlCommand(sql.ToString(), conn);
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);

        var columnCount = reader.FieldCount;
        long rowCount   = 0;

        await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));

        /* Header row */
        var header = new StringBuilder();
        for (int i = 0; i < columnCount; i++)
        {
            if (i > 0) header.Append('\t');
            header.Append(reader.GetName(i));
        }
        await writer.WriteLineAsync(header.ToString());

        /* Data rows */
        while (await reader.ReadAsync())
        {
            var line = new StringBuilder();
            for (int i = 0; i < columnCount; i++)
            {
                if (i > 0) line.Append('\t');
                line.Append(reader.IsDBNull(i) ? nullText : FormatValue(reader.GetValue(i)));
            }
            await writer.WriteLineAsync(line.ToString());
            rowCount++;

            if (rowCount % 100_000 == 0)
                Console.WriteLine($"[{label}] {rowCount:N0} rows exported...");
        }

        Console.WriteLine($"[{label}] Exported {rowCount:N0} rows, {columnCount} columns -> {Path.GetFileName(outputPath)}");
    }

    private static async Task<string?> DetectPrimaryKeyColumnsAsync(NpgsqlConnection conn, string schema, string table)
    {
        const string sql = """
            SELECT a.attname
            FROM   pg_index i
            JOIN   pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey)
            WHERE  i.indrelid = ($1 || '.' || $2)::regclass
            AND    i.indisprimary
            ORDER BY array_position(i.indkey, a.attnum)
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schema);
        cmd.Parameters.AddWithValue(table);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add($"\"{reader.GetString(0)}\"");

        return columns.Count > 0 ? string.Join(", ", columns) : null;
    }

    /// <summary>
    /// Returns column names that are identity columns or have a sequence-based default (serial types).
    /// </summary>
    private static async Task<List<string>> DetectGeneratedColumnsAsync(NpgsqlConnection conn, string schema, string table)
    {
        const string sql = """
            SELECT c.column_name
            FROM   information_schema.columns c
            WHERE  c.table_schema = $1
            AND    c.table_name   = $2
            AND    (
                       c.is_identity = 'YES'
                    OR c.column_default LIKE 'nextval(%'
                    OR c.is_generated = 'ALWAYS'
                   )
            ORDER BY c.ordinal_position
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schema);
        cmd.Parameters.AddWithValue(table);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));

        return columns;
    }

    /// <summary>
    /// Returns all column names for a table in ordinal order.
    /// </summary>
    private static async Task<List<string>> GetAllColumnNamesAsync(NpgsqlConnection conn, string schema, string table)
    {
        const string sql = """
            SELECT c.column_name
            FROM   information_schema.columns c
            WHERE  c.table_schema = $1
            AND    c.table_name   = $2
            ORDER BY c.ordinal_position
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(schema);
        cmd.Parameters.AddWithValue(table);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));

        return columns;
    }

    private static string ReadPasswordFromConsole(string prompt)
    {
        Console.Write(prompt);
        var password = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Remove(password.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        return password.ToString();
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            DateTime dt     => dt.ToString("O"),
            DateTimeOffset dto => dto.ToString("O"),
            byte[] bytes    => Convert.ToHexString(bytes),
            bool b          => b ? "true" : "false",
            _               => EscapeTsvField(value.ToString() ?? string.Empty)
        };
    }

    private static string EscapeTsvField(string value)
    {
        /* Replace tabs and newlines so TSV structure is preserved */
        return value
            .Replace("\t", "\\t")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    private static readonly string[] SystemSchemaPrefixes = ["pg_toast", "pg_temp", "pg_catalog"];

    private static bool IsSystemSchema(string schema)
    {
        if (string.Equals(schema, "information_schema", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var prefix in SystemSchemaPrefixes)
        {
            if (string.Equals(schema, prefix, StringComparison.OrdinalIgnoreCase))
                return true;
            if (schema.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

internal sealed record TableConfig(
    string Host,
    int    Port,
    string Database,
    string Schema,
    string Table,
    string User,
    string Password
);

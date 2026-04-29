namespace CachedCallUtils;

using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// A persistent, expression-based cache designed for interactive notebooks.
/// Primary purpose: Minimize calls to rate-limited Web APIs during exploration.
/// </summary>
public static class CachedCall
{
    public static bool CollocateCacheFile { get; set; } = true;

    public static string DbPath => CollocateCacheFile
        ? Path.Combine(Directory.GetCurrentDirectory(), "api_cache.db")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "InteractiveNotebooks", "call_cache.db");

    private static string ConnectionString => $"Data Source={DbPath};Cache=Shared;Mode=ReadWriteCreate;";
    #region Models

    public record CacheResult<T>(T Value, string RawJson, Exception? Error = null)
    {
        public bool IsSuccess => Error == null;
    }

    public record CacheMeta(string MethodName, string Tag, DateTime CreatedAt, int SizeBytes);

    public record CallMetadata(string Hash, string MethodName);

    #endregion

    #region Public Execution API

    /// <summary>
    /// Executes an async method and returns the result. 
    /// Set forceRefresh to true to bypass the cache and fetch fresh data.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Expression<Func<Task<T>>> expr,
        string tag = "default",
        bool forceRefresh = false)
    {
        var result = await ExecuteSafeAsync(expr, tag, forceRefresh);
        if (!result.IsSuccess) throw result.Error!;
        return result.Value;
    }

    /// <summary>
    /// Executes an async method with a Time-To-Live (TTL). 
    /// If the cached entry is older than maxAge, it re-fetches from the API.
    /// </summary>
    public static async Task<T> ExecuteWithExpiryAsync<T>(Expression<Func<Task<T>>> expr, TimeSpan maxAge, string tag = "default")
    {
        var meta = await GetMetadataAsync(expr);
        if (meta != null && (DateTime.UtcNow - meta.CreatedAt) > maxAge)
        {
            ClearSpecific(expr);
        }
        return await ExecuteAsync(expr, tag);
    }

    /// <summary>
    /// Executes an async method and returns a wrapper.
    /// Set forceRefresh to true to bypass the cache and fetch fresh data.
    /// </summary>
    public static async Task<CacheResult<T>> ExecuteSafeAsync<T>(
        Expression<Func<Task<T>>> expr,
        string tag = "default",
        bool forceRefresh = false)
    {
        EnsureDatabaseReady();
        var info = GetCallInfo(expr);

        // 1. Skip lookup if forceRefresh is true
        if (!forceRefresh)
        {
            string? json = await GetRawJsonInternal(info.Hash);
            if (json != null) return TryDeserialize<T>(json);
        }

        // 2. Fetch fresh data
        T result;
        string serializedJson;
        try
        {
            result = await expr.Compile()();
            serializedJson = JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return new CacheResult<T>(default!, null!, ex);
        }

        // 3. Save/Overwrite in DB
        await SaveToDbInternal(info, serializedJson, tag);

        return new CacheResult<T>(result, serializedJson);
    }

    #endregion

    #region Inspection & Metadata API

    /// <summary>
    /// Retrieves the raw JSON string from the database for a specific method call.
    /// </summary>
    public static async Task<string?> GetRawJsonAsync<T>(Expression<Func<Task<T>>> expr)
    {
        var info = GetCallInfo(expr);
        return await GetRawJsonInternal(info.Hash);
    }

    /// <summary>
    /// Checks if a specific call already exists in the cache.
    /// </summary>
    public static async Task<bool> ExistsAsync<T>(Expression<Func<Task<T>>> expr)
    {
        // Ensure the DB exists before we try to query stats
        EnsureDatabaseReady();

        var info = GetCallInfo(expr);
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM MethodCache WHERE CallHash = @hash LIMIT 1";
        cmd.Parameters.AddWithValue("@hash", info.Hash);
        return await cmd.ExecuteScalarAsync() != null;
    }

    /// <summary>
    /// Gets metadata (creation date, size, etc.) for a specific cached call.
    /// </summary>
    public static async Task<CacheMeta?> GetMetadataAsync<T>(Expression<Func<Task<T>>> expr)
    {
        // Ensure the DB exists before we try to query stats
        EnsureDatabaseReady();

        var info = GetCallInfo(expr);
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MethodName, Tag, CreatedAt, LENGTH(ResultJson) FROM MethodCache WHERE CallHash = @hash";
        cmd.Parameters.AddWithValue("@hash", info.Hash);

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new CacheMeta(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDateTime(2),
                reader.GetInt32(3)
            );
        }
        return null;
    }

    /// <summary>
    /// Prints summary statistics of the global cache to the console.
    /// </summary>
    public static void PrintStats()
    {
        // Ensure the DB exists before we try to query stats
        EnsureDatabaseReady();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
        SELECT 
            COUNT(*) as TotalItems, 
            SUM(LENGTH(ResultJson)) / 1024.0 / 1024.0 as SizeMB 
        FROM MethodCache";

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            // Get the values, handling potential NULLs if the table is empty
            long count = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            double sizeMb = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);

            Console.WriteLine("--- Notebook Cache Stats ---");
            Console.WriteLine($"Location:   {DbPath}");
            Console.WriteLine($"Items:      {count}");
            Console.WriteLine($"Total Size: {sizeMb:F2} MB");

            if (CollocateCacheFile)
            {
                Console.WriteLine("Status:     [Collocated with Notebook]");
            }
            else
            {
                Console.WriteLine("Status:     [Global LocalAppData]");
            }
        }
    }

    #endregion

    #region Management & Export API

    public static void ClearAll() => ExecuteNonQuery("DELETE FROM MethodCache");

    public static void ClearByTag(string tag) =>
        ExecuteNonQuery("DELETE FROM MethodCache WHERE Tag = @p", tag);

    public static void ClearSpecific<T>(Expression<Func<Task<T>>> expr)
    {
        var info = GetCallInfo(expr);
        ExecuteNonQuery("DELETE FROM MethodCache WHERE CallHash = @p", info.Hash);
    }

    /// <summary>
    /// Exports all cached items with a specific tag to a JSON file.
    /// </summary>
    public static async Task ExportTagAsync(string tag, string filePath)
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ResultJson FROM MethodCache WHERE Tag = @tag";
        cmd.Parameters.AddWithValue("@tag", tag);

        var results = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(JsonSerializer.Deserialize<object>(reader.GetString(0))!);
        }

        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Exported {results.Count} items to {filePath}");
    }

    /// <summary>
    /// Prints summary statistics of the global cache to the console.
    /// </summary>
    #endregion

    #region Internal Logic

    private static bool dbReady = false;
    private static void EnsureDatabaseReady()
    {
        if (dbReady) return;

        // Directory creation is cheap and safe to call multiple times
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        // This is the "Safe" way to enable WAL mode with Microsoft.Data.Sqlite
        var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
        pragmaCmd.ExecuteNonQuery();

        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS MethodCache (
                CallHash TEXT PRIMARY KEY, MethodName TEXT, Tag TEXT, ResultJson TEXT, CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_tag ON MethodCache(Tag);";
        cmd.ExecuteNonQuery();

        dbReady = true;
    }

    public static CallMetadata GetCallInfo<T>(Expression<Func<T>> expr)
    {
        if (expr.Body is not MethodCallExpression mce)
            throw new ArgumentException("Expression must be a method call.");

        var declaringType = mce.Method.DeclaringType;

        // The Namespace Check: 
        // Top-level notebook methods have no namespace and get wrapped in Submission#X.
        // Library methods (DLLs/Packages) have namespaces we can trust.
        string typeName = declaringType is null || string.IsNullOrEmpty(declaringType.Namespace)
            ? "Notebook"
            : declaringType.Name;

        string methodName = mce.Method.Name;

        // The "Human Readable" name for the DB column
        string displayName = $"{typeName}.{methodName}";

        // 1. Parameter Types (for signature uniqueness)
        string paramTypes = string.Join(",", mce.Method.GetParameters()
            .Select(p => p.ParameterType.Name));

        // 2. Argument Values (for call uniqueness)
        var argValues = mce.Arguments.Select(arg =>
            Expression.Lambda(arg).Compile().DynamicInvoke()
        ).ToArray();

        string serializedArgs = JsonSerializer.Serialize(argValues);

        // 3. The "Strict" Internal Key for hashing
        string rawKey = $"{displayName}({paramTypes})_{serializedArgs}";

        string hash = BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)))
            .Replace("-", "").ToLower();

        // Pass the displayName (Class.Method) into the metadata
        return new CallMetadata(hash, displayName);
    }

    private static async Task<string?> GetRawJsonInternal(string hash)
    {
        EnsureDatabaseReady();

        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ResultJson FROM MethodCache WHERE CallHash = @hash";
        cmd.Parameters.AddWithValue("@hash", hash);
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }

    private static async Task SaveToDbInternal(CallMetadata info, string json, string tag)
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO MethodCache (CallHash, MethodName, Tag, ResultJson) 
            VALUES (@hash, @name, @tag, @json)";
        cmd.Parameters.AddWithValue("@hash", info.Hash);
        cmd.Parameters.AddWithValue("@name", info.MethodName);
        cmd.Parameters.AddWithValue("@tag", tag);
        cmd.Parameters.AddWithValue("@json", json);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void ExecuteNonQuery(string sql, string? param = null)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (param != null) cmd.Parameters.AddWithValue("@p", param);
        cmd.ExecuteNonQuery();
    }

    private static CacheResult<T> TryDeserialize<T>(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json);
            return new CacheResult<T>(value!, json);
        }
        catch (JsonException ex)
        {
            return new CacheResult<T>(default!, json, ex);
        }
    }

    #endregion
}

# CachedCallUtils

A lightweight, expression-based caching utility for .NET designed to minimize API calls during interactive notebook exploration and development. Built with persistent SQLite storage and expression-based call hashing.

## Features

- **Expression-Based Caching**: Cache API calls using C# lambda expressions for type-safe, declarative syntax
- **Persistent Storage**: SQLite database backend for reliable, persistent cache across sessions
- **TTL Support**: Set time-to-live (TTL) expiration for cached entries to ensure data freshness
- **Error Handling**: Safe execution with exception capturing and result wrapping
- **Flexible Storage**: Choose between collocated cache files (with notebooks) or global LocalAppData storage
- **Cache Management**: Clear all, clear by tag, or clear specific cached calls
- **Export Functionality**: Export cached items by tag to JSON files
- **Statistics**: View cache statistics including item count and total storage size
- **WAL Mode**: SQLite Write-Ahead Logging for improved concurrency and reliability

## Installation

### Manual
Clone the repository and add the project reference to your solution.

## Quick Start

### Basic Usage

```csharp
using CachedCallUtils;

// Cache an async API call
var result = await CachedCall.ExecuteAsync(
    () => FetchUserDataAsync(userId),
    tag: "user-data"
);

// Access cached data (from DB if available)
var cachedResult = await CachedCall.ExecuteAsync(
    () => FetchUserDataAsync(userId),
    tag: "user-data"
);
```

### Force Refresh

```csharp
// Bypass cache and fetch fresh data
var freshData = await CachedCall.ExecuteAsync(
    () => FetchUserDataAsync(userId),
    tag: "user-data",
    forceRefresh: true
);
```

### TTL (Time-To-Live) Support

```csharp
// Data expires after 1 hour
var result = await CachedCall.ExecuteWithExpiryAsync(
    () => FetchUserDataAsync(userId),
    maxAge: TimeSpan.FromHours(1),
    tag: "user-data"
);
```

### Safe Execution (Handles Exceptions)

```csharp
// Returns a result wrapper that captures exceptions
var result = await CachedCall.ExecuteSafeAsync(
    () => FetchUserDataAsync(userId),
    tag: "user-data"
);

if (result.IsSuccess)
{
    Console.WriteLine($"Success: {result.Value}");
}
else
{
    Console.WriteLine($"Error: {result.Error.Message}");
}
```

## API Reference

### Execution Methods

#### `ExecuteAsync<T>(Expression<Func<Task<T>>> expr, string tag = "default", bool forceRefresh = false)`
Executes an async method and returns the result. Throws exceptions on failure.

#### `ExecuteSafeAsync<T>(Expression<Func<Task<T>>> expr, string tag = "default", bool forceRefresh = false)`
Executes an async method and returns a `CacheResult<T>` wrapper containing the result or exception.

#### `ExecuteWithExpiryAsync<T>(Expression<Func<Task<T>>> expr, TimeSpan maxAge, string tag = "default")`
Executes an async method with TTL. Automatically refreshes cache if older than `maxAge`.

### Inspection & Metadata

#### `GetRawJsonAsync<T>(Expression<Func<Task<T>>> expr)`
Retrieves the raw JSON string stored in the cache for a specific call.

#### `ExistsAsync<T>(Expression<Func<Task<T>>> expr)`
Checks if a specific call is cached.

#### `GetMetadataAsync<T>(Expression<Func<Task<T>>> expr)`
Returns metadata including creation date, tag, and cache size in bytes.

#### `PrintStats()`
Prints cache statistics to the console (item count, total size in MB, storage location).

### Cache Management

#### `ClearAll()`
Deletes all cached items.

#### `ClearByTag(string tag)`
Deletes all cached items with a specific tag.

#### `ClearSpecific<T>(Expression<Func<Task<T>>> expr)`
Deletes a specific cached call.

#### `ExportTagAsync(string tag, string filePath)`
Exports all items with a specific tag to a JSON file.

### Configuration

#### `CollocateCacheFile`
Property to control cache storage location:
- `true` (default): Stores cache file in the current working directory as `api_cache.db`
- `false`: Stores in `%LOCALAPPDATA%/InteractiveNotebooks/call_cache.db`

```csharp
CachedCall.CollocateCacheFile = false; // Use global AppData location
```

## Data Models

### `CacheResult<T>`
```csharp
public record CacheResult<T>(T Value, string RawJson, Exception? Error = null)
{
    public bool IsSuccess => Error == null;
}
```
Wraps execution results with optional exception information.

### `CacheMeta`
```csharp
public record CacheMeta(string MethodName, string Tag, DateTime CreatedAt, int SizeBytes);
```
Contains metadata about a cached entry.

## How It Works

1. **Expression Parsing**: Analyzes the lambda expression to extract the method name and arguments
2. **Hash Generation**: Creates a SHA256 hash of the method name and serialized arguments for the cache key
3. **Database Lookup**: Checks SQLite for existing cached results
4. **Execution**: If not cached, executes the async method
5. **Serialization**: Results are serialized to JSON and stored in the database
6. **Return**: Returns the result directly or wrapped in `CacheResult<T>`

## Requirements

- **.NET 10.0** or later
- **Microsoft.Data.Sqlite.Core** (included as dependency)

## Use Cases

- **Interactive Notebooks**: Minimize repeated API calls during data exploration in Jupyter or Polyglot notebooks
- **Development**: Speed up development workflows by caching expensive API responses
- **Rate-Limited APIs**: Work around API rate limits by caching responses during testing
- **Offline Development**: Access cached data when API is temporarily unavailable

## Database Schema

The cache uses a simple SQLite table:

```sql
CREATE TABLE MethodCache (
    CallHash TEXT PRIMARY KEY,
    MethodName TEXT,
    Tag TEXT,
    ResultJson TEXT,
    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_tag ON MethodCache(Tag);
```

## Performance Considerations

- **SHA256 Hashing**: Used for reliable, collision-resistant cache keys
- **WAL Mode**: Enables concurrent reads and writes without locking
- **Lazy Initialization**: Database and schema created on first use
- **In-Memory Caching**: Results are immediately available without database round trips on cache hits

## Error Handling

The library provides two approaches to error handling:

1. **Exceptions** (ExecuteAsync): Throws on execution failure
2. **Safe Wrapping** (ExecuteSafeAsync): Returns exceptions in `CacheResult<T>` for graceful handling

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests on [GitHub](https://github.com/konst-sh/CachedCallUtils).

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Related

- GitHub Repository: https://github.com/konst-sh/CachedCallUtils
- Target Framework: .NET 10.0
- Dependencies: Microsoft.Data.Sqlite.Core

## Examples

### Complete Example: Caching Weather API Data

```csharp
// Setup
CachedCall.CollocateCacheFile = true;

// First call - hits the API
var weather = await CachedCall.ExecuteAsync(
    () => weatherService.GetWeatherAsync("New York"),
    tag: "weather"
);

// Second call - returns cached data
var cachedWeather = await CachedCall.ExecuteAsync(
    () => weatherService.GetWeatherAsync("New York"),
    tag: "weather"
);

// Force refresh - bypasses cache
var freshWeather = await CachedCall.ExecuteAsync(
    () => weatherService.GetWeatherAsync("New York"),
    tag: "weather",
    forceRefresh: true
);

// TTL-based refresh - updates if older than 1 hour
var updatedWeather = await CachedCall.ExecuteWithExpiryAsync(
    () => weatherService.GetWeatherAsync("New York"),
    maxAge: TimeSpan.FromHours(1),
    tag: "weather"
);

// View statistics
CachedCall.PrintStats();

// Export cached data
await CachedCall.ExportTagAsync("weather", "weather_data.json");

// Cleanup
CachedCall.ClearByTag("weather");
```

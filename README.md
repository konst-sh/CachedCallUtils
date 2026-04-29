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

### Safe Execution (Handles Exceptions)

```csharp
// Returns a result wrapper that captures exceptions on call cache deserialization or method execution
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

### TTL (Time-To-Live) Support

See [Notebook Best Practices](#notebook-best-practices) section for TTL usage examples.

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
- `true` (default): Stores cache file in the current working directory as `call_cache.db`
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

### Execution Flow

1. **Database Initialization**: On first use, creates SQLite database and schema in the configured location
2. **Expression Validation**: Ensures the expression is a valid method call
3. **Cache Key Generation**: Computes the SHA256 hash (see [Cache Key Details](#cache-key-details) section)
4. **Cache Lookup**: Checks if the key exists in the database
   - If `forceRefresh` is `true`: Skips lookup and fetches fresh data
   - If cache hit: Returns or wraps the stored JSON result
   - If cache miss: Proceeds to execution
5. **Method Execution**: Invokes the async method and captures the result
6. **Serialization**: Converts the result to JSON using `JsonSerializer.Serialize()`
7. **Storage**: Saves to database with method name, tag, and creation timestamp
8. **Return**: Returns the result directly or wrapped in `CacheResult<T>`

### Interactive Notebook Integration

CachedCallUtils is optimized for interactive notebook workflows with automatic support for notebook method disambiguation (see [Cache Key Details - Notebook Disambiguation](#interactive-notebook-disambiguation-logic)).

**Storage Options:**
- **Collocated** (default): Cache stored in current directory as `call_cache.db` and shared across notebooks in the same directory
- **Global**: Cache stored in `%LOCALAPPDATA%/InteractiveNotebooks/call_cache.db` and shared across all notebooks on the machine

Configure via: `CachedCall.CollocateCacheFile = true/false;`

See [Notebook Best Practices](#notebook-best-practices) for workflow examples.

## Requirements

- **.NET 10.0** or later
- **Microsoft.Data.Sqlite.Core**

## Use Cases

- **Interactive Notebooks**: Minimize repeated API calls during data exploration in Jupyter, Polyglot or Verso notebooks
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

## Cache Key Details

Cache keys are deterministic and uniquely identify method calls based on the declaring class, method name, parameter types, and argument values.

### Key Generation Process

The cache key is generated through these steps:

1. **Extract Method Context**:
   - Get the declaring class/type name
   - Get the method name
   - Extract parameter type names (e.g., `String`, `Int32`, `List`)
   - Extract argument values

2. **Construct Raw Key String**:
   ```
   {ClassName}.{MethodName}({ParamType1},{ParamType2})_{SerializedArgumentValues}
   ```

3. **Hash the Raw Key**:
   - Apply SHA256 hashing to create a 64-character hexadecimal collision-resistant key
   - This hash becomes the primary database key

**Example:**
```csharp
// Expression: () => userService.GetUserAsync(42)
// Raw key: UserService.GetUserAsync(Int32)_[42]
// Cache key: a1b2c3d4e5f6...7f (SHA256 hash)
```

### Interactive Notebook Disambiguation Logic

When expressions are evaluated in interactive notebooks (like Polyglot, Jupyter, or FSI), notebook methods are automatically generated with namespace-like names (`Submission#1`, `Submission#2`, etc.). CachedCallUtils handles this with automatic disambiguation:

**Detection Logic:**
- If the declaring type has **no namespace** or an **empty namespace**, it's treated as a notebook method
- Notebook methods are normalized to use `"Notebook"` as the type name instead of `Submission#X`
- Library methods (from DLLs/packages) with proper namespaces use their actual class name

**Benefits:**
- Cache is persistent across notebook cell resubmissions (which generate different Submission#X names)
- Identical code in different notebook sessions reuses cached data
- Library method calls remain distinct and properly typed

**Example:**
```csharp
// In Notebook Session 1: Submission#1.FetchDataAsync(...)
// In Notebook Session 2: Submission#2.FetchDataAsync(...)
// Both normalize to: Notebook.FetchDataAsync(...)
// Result: SAME cache key, data is reused across sessions

// vs. Library code always maintains identity:
// UserService.FetchDataAsync(...) 
// Always uses: UserService.FetchDataAsync(...)
// Result: Consistent cache behavior
```

### Cache Key Composition

The raw key includes four components before SHA256 hashing:

| Component | Example | Purpose |
|-----------|---------|---------|
| **Class Name** | `UserService` (or `Notebook` for notebook methods) | Identifies the class/module |
| **Method Name** | `GetUserAsync` | Identifies the method |
| **Parameter Types** | `Int32,String` | Ensures different overloads differ |
| **Argument Values** | `[42,"admin"]` (JSON serialized) | Ensures different arguments differ |

This means overloaded methods produce different cache keys: `GetAsync(42)` ≠ `GetAsync("42")`

### Deterministic Cache Keys

Keys are deterministic - identical calls always produce the same key:

```csharp
// SAME cache key (returns cached data)
await CachedCall.ExecuteAsync(() => GetUserAsync(42), tag: "users");
await CachedCall.ExecuteAsync(() => GetUserAsync(42), tag: "users");

// DIFFERENT cache keys (different arguments or types)
await CachedCall.ExecuteAsync(() => GetUserAsync(42), tag: "users");
await CachedCall.ExecuteAsync(() => GetUserAsync(99), tag: "users");
await CachedCall.ExecuteAsync(() => GetUserAsync("42"), tag: "users");  // string vs int
```

### Argument Serialization

Arguments are JSON-serialized as part of the cache key:

- **Primitive types** (int, string, bool): Directly serializable
- **Complex objects**: Add `[JsonSerializable]` attribute if needed
- **Collections**: Fully serialized (e.g., `[1,2,3]` vs `[1,2,3,4]` = different keys)
- **Null values**: Distinct from default values (`null ≠ 0`)

## Performance Considerations

- **SHA256 Hashing**: Used for reliable, collision-resistant cache keys (< 1ms overhead)
- **WAL Mode**: Enables concurrent reads and writes without locking the database
- **Lazy Initialization**: Database and schema created on first use
- **JSON Serialization**: Arguments and results serialized once during execution
- **Expression Compilation**: Lambda compiled once per execution
- **Disk I/O**: Cached lookups involve minimal SQLite queries

## Error Handling

The library provides two approaches to error handling:

1. **Exceptions** (ExecuteAsync): Throws on execution failure
2. **Safe Wrapping** (ExecuteSafeAsync): Returns exceptions in `CacheResult<T>` for graceful handling

## Notebook Best Practices

When using CachedCallUtils in interactive notebooks (Jupyter, Polyglot, Verso or other notebook environments):

**1. Set Storage Mode Early**
```csharp
// In the first cell of your notebook
CachedCall.CollocateCacheFile = true;  // Keep cache with notebook file
```

**2. Use Meaningful Tags for Organization**
```csharp
// Organize cache by data source or analysis phase
var users = await CachedCall.ExecuteAsync(
    () => apiClient.GetUsersAsync(),
    tag: "data-collection-phase"
);
```

**3. Monitor Cache Growth**
```csharp
CachedCall.PrintStats();  // View cache statistics
await CachedCall.ExportTagAsync("phase-1", "phase1_results.json");
CachedCall.ClearByTag("phase-1");
```

**4. Use TTL for Data That Might Change**
```csharp
// Ensure data freshness for evolving APIs
var currentData = await CachedCall.ExecuteWithExpiryAsync(
    () => apiClient.GetCurrentStatusAsync(),
    maxAge: TimeSpan.FromHours(1),
    tag: "status"
);
```

**5. Force Refresh During Development**
```csharp
// Test API changes without cache interference
var freshData = await CachedCall.ExecuteAsync(
    () => apiClient.GetDataAsync(),
    tag: "dev-test",
    forceRefresh: true
);
```

**6. Clean Up Between Sessions**
```csharp
// At the end of your analysis
CachedCall.PrintStats();
CachedCall.ClearByTag("exploration");  // Optional cleanup
```

## License

This project is licensed under the MIT License.

## Related

- GitHub Repository: https://github.com/konst-sh/CachedCallUtils
- Target Framework: .NET 10.0
- Dependencies: Microsoft.Data.Sqlite.Core

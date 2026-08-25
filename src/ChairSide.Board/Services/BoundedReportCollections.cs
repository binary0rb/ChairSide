using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Data.Sqlite;

namespace ChairSide.Board.Services;

internal static class ReportSpoolJson
{
    internal static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object)
            {
                return;
            }

            if (typeInfo.Type == typeof(CompletedRoomCycle))
            {
                AddHistoricalProjection(
                    typeInfo,
                    value => ((CompletedRoomCycle)value).ReportingProjection,
                    (value, projection) => ((CompletedRoomCycle)value).ReportingProjection = projection);
            }
            else if (typeInfo.Type == typeof(AbortedRoomAssignment))
            {
                AddHistoricalProjection(
                    typeInfo,
                    value => ((AbortedRoomAssignment)value).ReportingProjection,
                    (value, projection) => ((AbortedRoomAssignment)value).ReportingProjection = projection);
            }
        });

        return new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            TypeInfoResolver = resolver
        };
    }

    private static void AddHistoricalProjection(
        JsonTypeInfo typeInfo,
        Func<object, HistoricalReportingProjection?> get,
        Action<object, HistoricalReportingProjection?> set)
    {
        var property = typeInfo.CreateJsonPropertyInfo(
            typeof(HistoricalReportingProjection),
            "$historicalReportingProjection");
        property.Get = get;
        property.Set = (value, projection) => set(value, (HistoricalReportingProjection?)projection);
        typeInfo.Properties.Add(property);
    }
}

/// <summary>
/// Exact replayable collections used while composing reports from indefinite history. Small
/// populations stay in memory; larger populations spill to a private temporary SQLite file so
/// repeated calculations do not retain one managed object per historical encounter.
/// </summary>
internal static class BoundedReportCollections
{
    internal const int InMemoryThreshold = 100;

    internal static IReadOnlyList<T> Materialize<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var enumerator = source.GetEnumerator();
        var buffered = new List<T>(InMemoryThreshold + 1);
        while (buffered.Count <= InMemoryThreshold && enumerator.MoveNext())
        {
            buffered.Add(enumerator.Current);
        }

        if (buffered.Count <= InMemoryThreshold)
        {
            return buffered;
        }

        return DiskBackedReadOnlyList<T>.Create(Continue(buffered, enumerator));
    }

    internal static IReadOnlyList<T> OrderBy<T>(
        IEnumerable<T> source,
        Func<T, string?> primaryKey,
        bool descending,
        Func<T, long>? secondaryKey = null,
        bool secondaryDescending = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(primaryKey);
        var materialized = Materialize(source);
        if (materialized is not DiskBackedReadOnlyList<T>)
        {
            var ordered = descending
                ? materialized.OrderByDescending(primaryKey, StringComparer.Ordinal)
                : materialized.OrderBy(primaryKey, StringComparer.Ordinal);
            if (secondaryKey is not null)
            {
                ordered = secondaryDescending
                    ? ordered.ThenByDescending(secondaryKey)
                    : ordered.ThenBy(secondaryKey);
            }
            return ordered.ToList();
        }

        var orderedSpool = DiskBackedReadOnlyList<T>.CreateOrdered(
            materialized,
            primaryKey,
            descending,
            secondaryKey,
            secondaryDescending);
        (materialized as IDisposable)?.Dispose();
        return orderedSpool;
    }

    internal static IReadOnlyList<T> TakeTop<T, TKey>(
        IEnumerable<T> source,
        int limit,
        Func<T, TKey> keySelector,
        IComparer<TKey>? comparer = null)
    {
        if (limit <= 0) return [];
        comparer ??= Comparer<TKey>.Default;
        var retained = new List<T>(limit + 1);
        foreach (var item in source)
        {
            retained.Add(item);
            if (retained.Count > limit)
            {
                retained = retained
                    .OrderByDescending(keySelector, comparer)
                    .Take(limit)
                    .ToList();
            }
        }
        return retained.OrderByDescending(keySelector, comparer).ToList();
    }

    internal static double Average(IEnumerable<int?> values)
    {
        long sum = 0;
        var count = 0;
        foreach (var value in values)
        {
            if (!value.HasValue) continue;
            sum += value.Value;
            count++;
        }
        return count == 0 ? 0d : (double)sum / count;
    }

    internal static double? Median(IEnumerable<int?> values)
    {
        using var ordered = NumericOrderStatistics.Create(values.Where(value => value.HasValue).Select(value => (double)value!.Value));
        return ordered.Median;
    }

    internal static double? Median(IEnumerable<int> values)
    {
        using var ordered = NumericOrderStatistics.Create(values.Select(value => (double)value));
        return ordered.Median;
    }

    internal static double? Median(IEnumerable<double> values)
    {
        using var ordered = NumericOrderStatistics.Create(values);
        return ordered.Median;
    }

    internal static (double? Lower, double? Upper) Type7Quartiles(IEnumerable<double> values)
    {
        using var ordered = NumericOrderStatistics.Create(values);
        if (ordered.Count == 0) return (null, null);
        return (ordered.Type7Quantile(0.25d), ordered.Type7Quantile(0.75d));
    }

    private static IEnumerable<T> Continue<T>(IEnumerable<T> buffered, IEnumerator<T> remainder)
    {
        foreach (var item in buffered) yield return item;
        while (remainder.MoveNext()) yield return remainder.Current;
    }
}

internal sealed class ReportSpoolScope : IDisposable
{
    private static readonly AsyncLocal<ReportSpoolScope?> Ambient = new();
    private readonly ReportSpoolScope? _parent;
    private readonly HashSet<IDisposable> _owned = [];

    private ReportSpoolScope()
    {
        _parent = Ambient.Value;
        Ambient.Value = this;
    }

    internal static ReportSpoolScope? Current => Ambient.Value;

    internal static ReportSpoolScope Begin() => new();

    internal void Register(IDisposable spool) => _owned.Add(spool);

    internal void Retain(object? value)
    {
        if (value is IDisposable disposable) _owned.Remove(disposable);
    }

    public void Dispose()
    {
        foreach (var spool in _owned.Reverse()) spool.Dispose();
        _owned.Clear();
        Ambient.Value = _parent;
    }
}

internal sealed class DiskBackedReadOnlyList<T> : IReadOnlyList<T>, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = ReportSpoolJson.CreateOptions();
    private readonly string _path;
    private readonly string _orderBy;
    private bool _disposed;

    private DiskBackedReadOnlyList(string path, int count, string orderBy)
    {
        _path = path;
        Count = count;
        _orderBy = orderBy;
        ReportSpoolScope.Current?.Register(this);
    }

    public int Count { get; }

    public T this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            using var connection = Open(_path, SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT payload FROM items ORDER BY {_orderBy} LIMIT 1 OFFSET $offset;";
            command.Parameters.AddWithValue("$offset", index);
            return Deserialize((string)command.ExecuteScalar()!);
        }
    }

    internal static DiskBackedReadOnlyList<T> Create(IEnumerable<T> source) =>
        CreateCore(source, null, false, null, false);

    internal static DiskBackedReadOnlyList<T> CreateOrdered(
        IEnumerable<T> source,
        Func<T, string?> primaryKey,
        bool descending,
        Func<T, long>? secondaryKey,
        bool secondaryDescending) =>
        CreateCore(source, primaryKey, descending, secondaryKey, secondaryDescending);

    private static DiskBackedReadOnlyList<T> CreateCore(
        IEnumerable<T> source,
        Func<T, string?>? primaryKey,
        bool descending,
        Func<T, long>? secondaryKey,
        bool secondaryDescending)
    {
        var path = Path.Combine(Path.GetTempPath(), $"chairside-report-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = """
                    CREATE TABLE items (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        payload TEXT NOT NULL,
                        sort_text TEXT NULL,
                        sort_number INTEGER NULL
                    );
                    """;
                schema.ExecuteNonQuery();
            }

            var count = 0;
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO items(payload, sort_text, sort_number) VALUES ($payload, $text, $number);";
            var payloadParameter = insert.Parameters.Add("$payload", SqliteType.Text);
            var textParameter = insert.Parameters.Add("$text", SqliteType.Text);
            var numberParameter = insert.Parameters.Add("$number", SqliteType.Integer);
            foreach (var item in source)
            {
                payloadParameter.Value = JsonSerializer.Serialize(item, JsonOptions);
                textParameter.Value = primaryKey?.Invoke(item) is { } text ? text : DBNull.Value;
                numberParameter.Value = secondaryKey is null ? DBNull.Value : secondaryKey(item);
                insert.ExecuteNonQuery();
                count++;
            }
            transaction.Commit();

            var order = primaryKey is null
                ? "id"
                : $"sort_text {(descending ? "DESC" : "ASC")}, sort_number {(secondaryDescending ? "DESC" : "ASC")}, id";
            return new DiskBackedReadOnlyList<T>(path, count, order);
        }
        catch
        {
            DeleteFiles(path);
            throw;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Enumerate().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IEnumerable<T> Enumerate()
    {
        using var connection = Open(_path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload FROM items ORDER BY {_orderBy};";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return Deserialize(reader.GetString(0));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeleteFiles(_path);
        GC.SuppressFinalize(this);
    }

    ~DiskBackedReadOnlyList() => DeleteFiles(_path);

    private static T Deserialize(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("A report spool row could not be deserialized.");

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void DeleteFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(candidate)) File.Delete(candidate);
            }
            catch (IOException)
            {
                // A serializer or reader may still be releasing the file. Finalization is best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Temporary-spool cleanup must not fail a report response.
            }
        }
    }
}

internal sealed class NumericOrderStatistics : IDisposable
{
    private readonly IReadOnlyList<double>? _memory;
    private readonly string? _path;

    private NumericOrderStatistics(IReadOnlyList<double> memory)
    {
        _memory = memory;
        Count = memory.Count;
    }

    private NumericOrderStatistics(string path, int count)
    {
        _path = path;
        Count = count;
    }

    internal int Count { get; }

    internal double? Median => Count == 0
        ? null
        : Count % 2 == 1
            ? ValueAt(Count / 2)
            : (ValueAt((Count / 2) - 1) + ValueAt(Count / 2)) / 2d;

    internal static NumericOrderStatistics Create(IEnumerable<double> source)
    {
        using var enumerator = source.GetEnumerator();
        var buffered = new List<double>(BoundedReportCollections.InMemoryThreshold + 1);
        while (buffered.Count <= BoundedReportCollections.InMemoryThreshold && enumerator.MoveNext())
        {
            buffered.Add(enumerator.Current);
        }
        if (buffered.Count <= BoundedReportCollections.InMemoryThreshold)
        {
            buffered.Sort();
            return new NumericOrderStatistics(buffered);
        }

        var path = Path.Combine(Path.GetTempPath(), $"chairside-stat-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = "CREATE TABLE values_spool(value REAL NOT NULL);";
                schema.ExecuteNonQuery();
            }
            var count = 0;
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO values_spool(value) VALUES ($value);";
            var parameter = insert.Parameters.Add("$value", SqliteType.Real);
            foreach (var value in Continue(buffered, enumerator))
            {
                parameter.Value = value;
                insert.ExecuteNonQuery();
                count++;
            }
            transaction.Commit();
            using var index = connection.CreateCommand();
            index.CommandText = "CREATE INDEX ix_values_spool_value ON values_spool(value);";
            index.ExecuteNonQuery();
            return new NumericOrderStatistics(path, count);
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            throw;
        }
    }

    internal double Type7Quantile(double probability)
    {
        if (Count == 0) throw new InvalidOperationException("No values are available.");
        var h = (Count - 1) * probability;
        var lowerIndex = (int)Math.Floor(h);
        var fraction = h - lowerIndex;
        var lower = ValueAt(lowerIndex);
        return fraction == 0d ? lower : lower + ((ValueAt(lowerIndex + 1) - lower) * fraction);
    }

    private double ValueAt(int index)
    {
        if (_memory is not null) return _memory[index];
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path!,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM values_spool ORDER BY value LIMIT 1 OFFSET $offset;";
        command.Parameters.AddWithValue("$offset", index);
        return Convert.ToDouble(command.ExecuteScalar());
    }

    public void Dispose()
    {
        if (_path is null) return;
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { }
    }

    private static IEnumerable<double> Continue(IEnumerable<double> buffered, IEnumerator<double> remainder)
    {
        foreach (var value in buffered) yield return value;
        while (remainder.MoveNext()) yield return remainder.Current;
    }
}

internal sealed class BoundedGroupingSet<T, TKey> : IDisposable
    where TKey : notnull
{
    private static readonly JsonSerializerOptions JsonOptions = ReportSpoolJson.CreateOptions();
    private readonly string? _path;

    private BoundedGroupingSet(IReadOnlyList<IGrouping<TKey, T>> groups, string? path)
    {
        Groups = groups;
        _path = path;
    }

    internal IReadOnlyList<IGrouping<TKey, T>> Groups { get; }

    internal static BoundedGroupingSet<T, TKey> Create(
        IReadOnlyList<T> source,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);
        comparer ??= EqualityComparer<TKey>.Default;
        if (source.Count <= BoundedReportCollections.InMemoryThreshold)
        {
            return new BoundedGroupingSet<T, TKey>(
                source.GroupBy(keySelector, comparer).ToList(),
                null);
        }

        var path = Path.Combine(Path.GetTempPath(), $"chairside-groups-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = """
                    CREATE TABLE grouped_items (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        group_id INTEGER NOT NULL,
                        payload TEXT NOT NULL
                    );
                    """;
                schema.ExecuteNonQuery();
            }

            var groupIds = new Dictionary<TKey, int>(comparer);
            var keys = new List<TKey>();
            using var transaction = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO grouped_items(group_id, payload) VALUES ($group, $payload);";
            var groupParameter = insert.Parameters.Add("$group", SqliteType.Integer);
            var payloadParameter = insert.Parameters.Add("$payload", SqliteType.Text);
            foreach (var item in source)
            {
                var key = keySelector(item);
                if (!groupIds.TryGetValue(key, out var groupId))
                {
                    groupId = keys.Count;
                    groupIds.Add(key, groupId);
                    keys.Add(key);
                }
                groupParameter.Value = groupId;
                payloadParameter.Value = JsonSerializer.Serialize(item, JsonOptions);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            using var index = connection.CreateCommand();
            index.CommandText = "CREATE INDEX ix_grouped_items_group ON grouped_items(group_id, id);";
            index.ExecuteNonQuery();

            var groups = keys
                .Select((key, groupId) => (IGrouping<TKey, T>)new DiskGrouping<TKey, T>(key, groupId, path))
                .ToList();
            return new BoundedGroupingSet<T, TKey>(groups, path);
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            throw;
        }
    }

    public void Dispose()
    {
        if (_path is null) return;
        try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { }
    }

    private sealed class DiskGrouping<TGroupKey, TItem>(TGroupKey key, int groupId, string path)
        : IGrouping<TGroupKey, TItem>
    {
        public TGroupKey Key { get; } = key;

        public IEnumerator<TItem> GetEnumerator() => Enumerate().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<TItem> Enumerate()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM grouped_items WHERE group_id = $group ORDER BY id;";
            command.Parameters.AddWithValue("$group", groupId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                yield return JsonSerializer.Deserialize<TItem>(reader.GetString(0), JsonOptions)
                    ?? throw new InvalidOperationException("A grouped report spool row could not be deserialized.");
            }
        }
    }
}

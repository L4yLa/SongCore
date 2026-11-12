using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SongCore.Utilities
{
    /// <summary>
    /// A dictionary cache that is persisted as a JSON snapshot plus an append only journal.
    /// <para>
    /// Rewriting the whole snapshot on every refresh is expensive on large libraries, so quick refreshes
    /// only append the entries that actually changed. The journal holds the changes made during the
    /// current session and is merged back into the snapshot on the next launch. Nothing is written when
    /// the game exits, because it frequently exits by crashing.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type of the cached values.</typeparam>
    internal sealed class JournaledCache<T> where T : class
    {
        private const string Magic = "SCCACHE";

        /// <summary>
        /// Oldest layout of the snapshot and journal files that can still be read. Each cache passes its own
        /// current layout, so that changing what one cache stores does not invalidate the others.
        /// </summary>
        private const int MinimumFormatVersion = 1;

        /// <summary>
        /// Meaning of the directory hash stored alongside each entry. Bump when the way it is computed
        /// changes, so that entries written by an older version are recomputed instead of trusted.
        /// </summary>
        internal const int ValidationVersion = 1;

        private const string OpSet = "set";
        private const string OpDelete = "del";

        private readonly string _name;
        private readonly int _formatVersion;
        private readonly string _snapshotPath;
        private readonly string _journalPath;
        private readonly object _journalLock = new object();
        private readonly object _loadLock = new object();
        private readonly object _snapshotLock = new object();
        private bool _loaded;
        private readonly ConcurrentDictionary<string, bool> _pendingChanges = new ConcurrentDictionary<string, bool>();

        // Null values and empty arrays are left out, which keeps the files considerably smaller since most
        // optional fields are unset. Other default values are still written, as some fields use [DefaultValue]
        // with Populate and would otherwise read back as something else.
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = OmitEmptyArraysContractResolver.Instance
        };

        // Whether the journal on disk was started by an older layout. Records are only appended to a journal
        // whose header matches what they are written in.
        private bool _journalHeaderOutdated;

        // Whether the snapshot on disk no longer matches the entries. A full refresh only rewrites the snapshot
        // when this is set, so that an unchanged library is not written out again on every launch.
        private volatile bool _snapshotOutdated;

        /// <param name="formatVersion">
        /// Current layout of this cache. Bump when what is stored changes in a way an older version could
        /// not read correctly. Older layouts are still read, and rewritten in the current one.
        /// </param>
        internal JournaledCache(string name, string snapshotPath, int formatVersion = MinimumFormatVersion)
        {
            _name = name;
            _formatVersion = formatVersion;
            _snapshotPath = snapshotPath;
            _journalPath = snapshotPath + ".journal";
        }

        /// <summary>
        /// The same dictionary for the whole session. Loading fills it in place rather than replacing it, since
        /// mods built against older versions may hold on to it through the fields that used to expose it.
        /// </summary>
        internal ConcurrentDictionary<string, T> Entries { get; } = new ConcurrentDictionary<string, T>();

        #region Reading

        /// <summary>
        /// Reads the snapshot, merges any journal left behind by the previous session, and makes sure an
        /// empty journal exists afterwards. The journal may be deleted by hand, which is treated the same
        /// as it being empty.
        /// </summary>
        /// <param name="validate">
        /// Returns whether a journal record is still valid. Applied to every record, never to the
        /// snapshot, since the snapshot was already validated when it was written.
        /// </param>
        internal void Load(Func<string, T?, bool> validate)
        {
            // Loading twice would read back what is on disk and drop every change not saved yet, so only the first
            // call loads. Later calls, such as from the old public API, wait for it to finish instead.
            lock (_loadLock)
            {
                if (_loaded)
                {
                    return;
                }

                LoadCore(validate);
                _loaded = true;
            }
        }

        private void LoadCore(Func<string, T?, bool> validate)
        {
            var stopwatch = Stopwatch.StartNew();

            ReadSnapshot();

            var merged = MergeJournal(validate);
            if (merged)
            {
                // The journal has been folded into the snapshot, so it can start over.
                WriteSnapshot();
            }
            else if (!File.Exists(_journalPath) || _journalHeaderOutdated)
            {
                WriteJournalHeader();
            }

            Plugin.Log.Info($"Loaded cached {_name} for {Entries.Count} songs in {stopwatch.Elapsed.TotalSeconds:0.###} seconds.");
        }

        private void ReadSnapshot()
        {
            // Cleared again below once a usable snapshot has been read.
            _snapshotOutdated = true;

            if (!File.Exists(_snapshotPath))
            {
                return;
            }

            try
            {
                using var reader = new JsonTextReader(new StreamReader(_snapshotPath));
                var snapshot = JsonSerializer.Create(SerializerSettings).Deserialize<Snapshot>(reader);
                if (snapshot == null)
                {
                    Plugin.Log.Warn($"Cached {_name} is empty. Rebuilding.");
                    return;
                }

                if (!IsHeaderCompatible(snapshot.magic, snapshot.format, snapshot.validation, "snapshot"))
                {
                    return;
                }

                if (snapshot.entries != null)
                {
                    ReplaceEntries(snapshot.entries);

                    // An older layout is kept as-is, but written again in the current one by the next save.
                    _snapshotOutdated = snapshot.format < _formatVersion;
                }
            }
            catch (Exception ex)
            {
                // Also covers the old headerless format, which is intentionally not migrated.
                Plugin.Log.Warn($"Could not read cached {_name}. Rebuilding. ({ex.Message})");
                Entries.Clear();
            }
        }

        private void ReplaceEntries(ConcurrentDictionary<string, T> entries)
        {
            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries[entry.Key] = entry.Value;
            }
        }

        /// <summary>
        /// Applies the journal on top of the snapshot. Records are applied in order, so the last record
        /// for a key wins. Anything that fails validation is dropped rather than trusted.
        /// </summary>
        private bool MergeJournal(Func<string, T?, bool> validate)
        {
            if (!File.Exists(_journalPath))
            {
                return false;
            }

            var applied = 0;
            var discarded = 0;

            try
            {
                using (var reader = new StreamReader(_journalPath))
                {
                    var headerLine = reader.ReadLine();
                    if (!TryReadHeader(headerLine))
                    {
                        Plugin.Log.Warn($"Journal for {_name} has an incompatible header. Discarding it.");
                        reader.Dispose();
                        DeleteJournal();
                        return false;
                    }

                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        JournalRecord? record;
                        try
                        {
                            record = JsonConvert.DeserializeObject<JournalRecord>(line, SerializerSettings);
                        }
                        catch (JsonException)
                        {
                            // A truncated line means the process died mid write, so everything past this
                            // point is unreliable as well.
                            discarded++;
                            Plugin.Log.Warn($"Journal for {_name} is truncated. Ignoring the remaining records.");
                            break;
                        }

                        if (!IsRecordValid(record, validate))
                        {
                            discarded++;
                            continue;
                        }

                        if (record!.op == OpSet)
                        {
                            Entries[record.k!] = record.v!;
                        }
                        else
                        {
                            Entries.TryRemove(record.k!, out _);
                        }

                        applied++;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error merging journal for {_name}:");
                Plugin.Log.Error(ex);
                DeleteJournal();
                return false;
            }

            if (discarded > 0)
            {
                Plugin.Log.Warn($"Discarded {discarded} invalid journal {(discarded == 1 ? "entry" : "entries")} for {_name}.");
            }

            if (applied > 0)
            {
                Plugin.Log.Info($"Merged {applied} journal {(applied == 1 ? "entry" : "entries")} into cached {_name}.");
            }

            return applied > 0 || discarded > 0;
        }

        private bool IsRecordValid(JournalRecord? record, Func<string, T?, bool> validate)
        {
            if (record == null)
            {
                return false;
            }

            if (record.op != OpSet && record.op != OpDelete)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(record.k))
            {
                return false;
            }

            // Keys are always stored with the platform separator, so anything else did not come from us.
            if (record.k!.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                return false;
            }

            if (record.op == OpSet && record.v == null)
            {
                return false;
            }

            return validate(record.k, record.v);
        }

        private bool TryReadHeader(string? headerLine)
        {
            if (headerLine == null)
            {
                return false;
            }

            try
            {
                var header = JsonConvert.DeserializeObject<Header>(headerLine);
                if (header == null || !IsHeaderCompatible(header.magic, header.format, header.validation, "journal"))
                {
                    return false;
                }

                _journalHeaderOutdated = header.format < _formatVersion;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private bool IsHeaderCompatible(string? magic, int format, int validation, string what)
        {
            if (magic != Magic)
            {
                Plugin.Log.Warn($"Cached {_name} {what} is not in a known format. Rebuilding.");
                return false;
            }

            if (format > _formatVersion)
            {
                // Written by a newer SongCore. Partially interpreting it would be worse than starting over.
                Plugin.Log.Warn($"Cached {_name} {what} was written by a newer version. Rebuilding.");
                return false;
            }

            if (format < MinimumFormatVersion || validation != ValidationVersion)
            {
                Plugin.Log.Warn($"Cached {_name} {what} is outdated. Rebuilding.");
                return false;
            }

            return true;
        }

        #endregion

        #region Writing

        /// <summary>
        /// Records that an entry was added or updated, so that it can be journaled after a quick refresh.
        /// </summary>
        internal void MarkChanged(string key)
        {
            _pendingChanges[key] = true;
            _snapshotOutdated = true;
        }

        /// <summary>
        /// Records that an entry was removed, so that it can be journaled after a quick refresh.
        /// </summary>
        internal void MarkDeleted(string key)
        {
            _pendingChanges[key] = false;
            _snapshotOutdated = true;
        }

        /// <summary>
        /// Records that entries were removed without being journaled, as done when pruning before a snapshot.
        /// </summary>
        internal void MarkOutdated()
        {
            _snapshotOutdated = true;
        }

        /// <summary>
        /// Writes the snapshot only if the entries changed since it was last written or read.
        /// </summary>
        internal void WriteSnapshotIfOutdated()
        {
            if (!_snapshotOutdated)
            {
                Plugin.Log.Info($"Cached {_name} is unchanged. Skipped saving.");
                return;
            }

            WriteSnapshot();
        }

        internal void ClearPendingChanges()
        {
            _pendingChanges.Clear();
        }

        /// <summary>
        /// Appends everything recorded since the last write to the journal.
        /// </summary>
        /// <returns>The number of records written. Logged by the caller, together with the other caches.</returns>
        internal int AppendPendingChanges()
        {
            if (_pendingChanges.IsEmpty)
            {
                return 0;
            }

            var setKeys = new List<string>();
            var deletedKeys = new List<string>();
            foreach (var change in _pendingChanges)
            {
                if (change.Value)
                {
                    setKeys.Add(change.Key);
                }
                else
                {
                    deletedKeys.Add(change.Key);
                }
            }

            _pendingChanges.Clear();
            return AppendToJournal(setKeys, deletedKeys);
        }

        /// <summary>
        /// Writes the full snapshot and resets the journal. Used after a full refresh, when every entry
        /// has been revalidated anyway.
        /// </summary>
        internal void WriteSnapshot()
        {
            // Saves can be requested from the old public API while SongCore is saving, and both would write to the
            // same temporary file.
            lock (_snapshotLock)
            {
                WriteSnapshotCore();
            }
        }

        private void WriteSnapshotCore()
        {
            var stopwatch = Stopwatch.StartNew();

            // Cleared before writing, so that a change recorded while the snapshot is being written is not lost.
            _snapshotOutdated = false;

            try
            {
                var tempPath = _snapshotPath + ".tmp";

                using (var streamWriter = new StreamWriter(tempPath, false))
                using (var jsonWriter = new JsonTextWriter(streamWriter))
                {
                    JsonSerializer.Create(SerializerSettings).Serialize(jsonWriter, new Snapshot
                    {
                        magic = Magic,
                        format = _formatVersion,
                        validation = ValidationVersion,
                        entries = Entries
                    });
                }

                if (File.Exists(_snapshotPath))
                {
                    // Replacing is atomic, so the cache cannot end up missing if the process dies here.
                    // Deleting and moving would leave a window where no file exists at all, which would
                    // cost a full rebuild on the next launch.
                    File.Replace(tempPath, _snapshotPath, null);
                }
                else
                {
                    File.Move(tempPath, _snapshotPath);
                }

                WriteJournalHeader();
                _pendingChanges.Clear();

                Plugin.Log.Info($"Saved cached {_name} for {Entries.Count} songs in {stopwatch.Elapsed.TotalSeconds:0.###} seconds.");
            }
            catch (Exception ex)
            {
                _snapshotOutdated = true;
                Plugin.Log.Error($"Error saving cached {_name}:");
                Plugin.Log.Error(ex);
            }
        }

        /// <summary>
        /// Appends the given changes to the journal, leaving the snapshot untouched. Used after a quick
        /// refresh, where only a handful of beatmaps have changed.
        /// </summary>
        /// <returns>The number of records written.</returns>
        private int AppendToJournal(IReadOnlyCollection<string> setKeys, IReadOnlyCollection<string> deletedKeys)
        {
            if (setKeys.Count == 0 && deletedKeys.Count == 0)
            {
                return 0;
            }

            var written = 0;

            try
            {
                lock (_journalLock)
                {
                    if (!File.Exists(_journalPath))
                    {
                        WriteJournalHeaderCore();
                    }

                    using var streamWriter = new StreamWriter(_journalPath, true);
                    foreach (var key in setKeys)
                    {
                        if (Entries.TryGetValue(key, out var value))
                        {
                            WriteRecord(streamWriter, new JournalRecord { op = OpSet, k = key, v = value });
                            written++;
                        }
                    }

                    foreach (var key in deletedKeys)
                    {
                        WriteRecord(streamWriter, new JournalRecord { op = OpDelete, k = key });
                        written++;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error journaling cached {_name}:");
                Plugin.Log.Error(ex);
            }

            return written;
        }

        private static void WriteRecord(TextWriter writer, JournalRecord record)
        {
            writer.WriteLine(JsonConvert.SerializeObject(record, Formatting.None, SerializerSettings));
        }

        private void WriteJournalHeader()
        {
            try
            {
                lock (_journalLock)
                {
                    WriteJournalHeaderCore();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error creating journal for {_name}:");
                Plugin.Log.Error(ex);
            }
        }

        private void WriteJournalHeaderCore()
        {
            _journalHeaderOutdated = false;
            using var streamWriter = new StreamWriter(_journalPath, false);
            streamWriter.WriteLine(JsonConvert.SerializeObject(new Header
            {
                magic = Magic,
                format = _formatVersion,
                validation = ValidationVersion
            }, Formatting.None));
        }

        private void DeleteJournal()
        {
            try
            {
                if (File.Exists(_journalPath))
                {
                    File.Delete(_journalPath);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error deleting journal for {_name}:");
                Plugin.Log.Error(ex);
            }
        }

        #endregion

        /// <summary>
        /// Leaves out arrays that are empty, but only for members that start out as an empty array. Those read
        /// back as an empty array when left out, so the data is the same after a round trip. Members that start
        /// out as null are always written, as null and empty can mean different things there.
        /// </summary>
        private sealed class OmitEmptyArraysContractResolver : DefaultContractResolver
        {
            internal static readonly OmitEmptyArraysContractResolver Instance = new OmitEmptyArraysContractResolver();

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var property = base.CreateProperty(member, memberSerialization);
                if (property.PropertyType is not { IsArray: true } || property.ValueProvider == null || !StartsOutEmpty(member, property.ValueProvider))
                {
                    return property;
                }

                var valueProvider = property.ValueProvider;
                var shouldSerialize = property.ShouldSerialize;
                property.ShouldSerialize = instance => valueProvider.GetValue(instance) is not Array { Length: 0 } && (shouldSerialize == null || shouldSerialize(instance));
                return property;
            }

            private static bool StartsOutEmpty(MemberInfo member, IValueProvider valueProvider)
            {
                var declaringType = member.DeclaringType;
                if (declaringType == null || declaringType.IsAbstract || declaringType.GetConstructor(Type.EmptyTypes) == null)
                {
                    return false;
                }

                try
                {
                    return valueProvider.GetValue(Activator.CreateInstance(declaringType)) is Array { Length: 0 };
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private class Header
        {
            public string? magic { get; set; }
            public int format { get; set; }
            public int validation { get; set; }
        }

        private class Snapshot : Header
        {
            public ConcurrentDictionary<string, T>? entries { get; set; }
        }

        private class JournalRecord
        {
            public string? op { get; set; }
            public string? k { get; set; }
            public T? v { get; set; }
        }
    }
}

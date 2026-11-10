using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

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
        /// Layout of the snapshot and journal files. Bump when the structure changes.
        /// </summary>
        private const int FormatVersion = 1;

        /// <summary>
        /// Meaning of the directory hash stored alongside each entry. Bump when the way it is computed
        /// changes, so that entries written by an older version are recomputed instead of trusted.
        /// </summary>
        internal const int ValidationVersion = 1;

        private const string OpSet = "set";
        private const string OpDelete = "del";

        private readonly string _name;
        private readonly string _snapshotPath;
        private readonly string _journalPath;
        private readonly object _journalLock = new object();
        private readonly ConcurrentDictionary<string, bool> _pendingChanges = new ConcurrentDictionary<string, bool>();

        internal JournaledCache(string name, string snapshotPath)
        {
            _name = name;
            _snapshotPath = snapshotPath;
            _journalPath = snapshotPath + ".journal";
        }

        internal ConcurrentDictionary<string, T> Entries { get; private set; } = new ConcurrentDictionary<string, T>();

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
            var stopwatch = Stopwatch.StartNew();

            ReadSnapshot();

            var merged = MergeJournal(validate);
            if (merged)
            {
                // The journal has been folded into the snapshot, so it can start over.
                WriteSnapshot();
            }
            else if (!File.Exists(_journalPath))
            {
                WriteJournalHeader();
            }

            Plugin.Log.Info($"Loaded cached {_name} for {Entries.Count} songs in {stopwatch.Elapsed.TotalSeconds:0.###} seconds.");
        }

        private void ReadSnapshot()
        {
            if (!File.Exists(_snapshotPath))
            {
                return;
            }

            try
            {
                using var reader = new JsonTextReader(new StreamReader(_snapshotPath));
                var snapshot = JsonSerializer.CreateDefault().Deserialize<Snapshot>(reader);
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
                    Entries = snapshot.entries;
                }
            }
            catch (Exception ex)
            {
                // Also covers the old headerless format, which is intentionally not migrated.
                Plugin.Log.Warn($"Could not read cached {_name}. Rebuilding. ({ex.Message})");
                Entries = new ConcurrentDictionary<string, T>();
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
                            record = JsonConvert.DeserializeObject<JournalRecord>(line);
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
                return header != null && IsHeaderCompatible(header.magic, header.format, header.validation, "journal");
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

            if (format > FormatVersion)
            {
                // Written by a newer SongCore. Partially interpreting it would be worse than starting over.
                Plugin.Log.Warn($"Cached {_name} {what} was written by a newer version. Rebuilding.");
                return false;
            }

            if (format != FormatVersion || validation != ValidationVersion)
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
        }

        /// <summary>
        /// Records that an entry was removed, so that it can be journaled after a quick refresh.
        /// </summary>
        internal void MarkDeleted(string key)
        {
            _pendingChanges[key] = false;
        }

        internal void ClearPendingChanges()
        {
            _pendingChanges.Clear();
        }

        /// <summary>
        /// Appends everything recorded since the last write to the journal.
        /// </summary>
        internal void AppendPendingChanges()
        {
            if (_pendingChanges.IsEmpty)
            {
                return;
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
            AppendToJournal(setKeys, deletedKeys);
        }

        /// <summary>
        /// Writes the full snapshot and resets the journal. Used after a full refresh, when every entry
        /// has been revalidated anyway.
        /// </summary>
        internal void WriteSnapshot()
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var tempPath = _snapshotPath + ".tmp";

                using (var streamWriter = new StreamWriter(tempPath, false))
                using (var jsonWriter = new JsonTextWriter(streamWriter))
                {
                    JsonSerializer.CreateDefault().Serialize(jsonWriter, new Snapshot
                    {
                        magic = Magic,
                        format = FormatVersion,
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
                Plugin.Log.Error($"Error saving cached {_name}:");
                Plugin.Log.Error(ex);
            }
        }

        /// <summary>
        /// Appends the given changes to the journal, leaving the snapshot untouched. Used after a quick
        /// refresh, where only a handful of beatmaps have changed.
        /// </summary>
        internal void AppendToJournal(IReadOnlyCollection<string> setKeys, IReadOnlyCollection<string> deletedKeys)
        {
            if (setKeys.Count == 0 && deletedKeys.Count == 0)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();

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
                        }
                    }

                    foreach (var key in deletedKeys)
                    {
                        WriteRecord(streamWriter, new JournalRecord { op = OpDelete, k = key });
                    }
                }

                Plugin.Log.Info($"Journaled {setKeys.Count + deletedKeys.Count} changed cached {_name} in {stopwatch.Elapsed.TotalSeconds:0.###} seconds.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error journaling cached {_name}:");
                Plugin.Log.Error(ex);
            }
        }

        private static void WriteRecord(TextWriter writer, JournalRecord record)
        {
            writer.WriteLine(JsonConvert.SerializeObject(record, Formatting.None));
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
            using var streamWriter = new StreamWriter(_journalPath, false);
            streamWriter.WriteLine(JsonConvert.SerializeObject(new Header
            {
                magic = Magic,
                format = FormatVersion,
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

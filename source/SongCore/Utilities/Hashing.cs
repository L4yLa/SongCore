using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BeatmapLevelSaveDataVersion4;
using SongCore.Data;

namespace SongCore.Utilities
{
    public class Hashing
    {
        public static readonly string cachedHashDataPath = Path.Combine(IPA.Utilities.UnityGame.UserDataPath, nameof(SongCore), "SongHashData.dat");
        public static readonly string cachedAudioDataPath = Path.Combine(IPA.Utilities.UnityGame.UserDataPath, nameof(SongCore), "SongDurationCache.dat");

        internal static readonly JournaledCache<SongHashData> SongHashCache = new JournaledCache<SongHashData>("hashes", cachedHashDataPath);
        internal static readonly JournaledCache<AudioCacheData> AudioCache = new JournaledCache<AudioCacheData>("durations", cachedAudioDataPath);

        // Fields rather than properties, as in 3.16.0, for mods that read them through reflection. The caches never
        // replace these dictionaries, so the fields always see the current entries. Keys are relative paths.
        internal static ConcurrentDictionary<string, SongHashData> cachedSongHashData = SongHashCache.Entries;
        internal static ConcurrentDictionary<string, AudioCacheData> cachedAudioData = AudioCache.Entries;

        private static readonly ConcurrentDictionary<string, string> AbsolutePathCache = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, string> RelativePathCache = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, long> DirectoryHashCache = new ConcurrentDictionary<string, long>();

        internal static Task LoadCachedSongHashesAsync()
        {
            // Journal records are only trusted if the beatmap folder still looks exactly the way it did
            // when the record was written, so changes made while SongCore was not running are picked up.
            return Task.Run(() => SongHashCache.Load((relativePath, value) => IsFolderUnchangedInternal(relativePath, value?.directoryHash)));
        }

        internal static Task LoadCachedAudioDataAsync()
        {
            return Task.Run(() => AudioCache.Load((relativePath, value) => IsFolderUnchangedInternal(relativePath, value?.directoryHash)));
        }

        /// <summary>
        /// For a set record, the folder has to exist and still hash to the stored value. For a delete
        /// record, the folder has to be gone, otherwise the deletion no longer reflects reality.
        /// </summary>
        internal static bool IsFolderUnchangedInternal(string relativePath, long? expectedDirectoryHash)
        {
            var absolutePath = GetAbsolutePath(relativePath);
            var exists = Directory.Exists(absolutePath);

            if (expectedDirectoryHash == null)
            {
                return !exists;
            }

            return exists && GetDirectoryHash(absolutePath) == expectedDirectoryHash.Value;
        }

        /// <summary>
        /// Kept for mods built against the old API. SongCore loads the cache itself before the first refresh, and
        /// reading it again from disk would drop changes that are not saved yet, so this only loads it if that has
        /// not happened yet, and waits for it otherwise.
        /// </summary>
        public static void ReadCachedSongHashes()
        {
            LoadCachedSongHashesAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Kept for mods built against the old API. Drops the cached hashes of beatmaps that are not in the given
        /// folders and saves the cache.
        /// </summary>
        public static void UpdateCachedHashes(HashSet<string> currentSongPaths)
        {
            PruneAndSave(SongHashCache, currentSongPaths);
        }

        /// <summary>
        /// Kept for mods built against the old API. See <see cref="ReadCachedSongHashes"/>.
        /// </summary>
        public static void ReadCachedAudioData()
        {
            LoadCachedAudioDataAsync().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Kept for mods built against the old API. Drops the cached durations of beatmaps that are not in the given
        /// folders and saves the cache.
        /// </summary>
        public static void UpdateCachedAudioData(HashSet<string> currentSongPaths)
        {
            PruneAndSave(AudioCache, currentSongPaths);
        }

        /// <summary>
        /// The internal method of 3.16.0, kept for mods that call it through reflection.
        /// </summary>
        internal static void UpdateCachedHashesInternal(ICollection<string> currentSongPaths)
        {
            PruneAndSave(SongHashCache, currentSongPaths);
        }

        /// <summary>
        /// The internal method of 3.16.0, kept for mods that call it through reflection.
        /// </summary>
        internal static void UpdateCachedAudioDataInternal(ICollection<string> currentSongPaths)
        {
            PruneAndSave(AudioCache, currentSongPaths);
        }

        internal static Task SaveCachedSongHashesAsync(ICollection<string> currentSongPaths)
        {
            return Task.Run(() => PruneAndSave(SongHashCache, currentSongPaths));
        }

        internal static Task SaveCachedAudioDataAsync(ICollection<string> currentSongPaths)
        {
            return Task.Run(() => PruneAndSave(AudioCache, currentSongPaths));
        }

        /// <summary>
        /// Drops entries for beatmaps that are no longer present and writes a fresh snapshot if anything changed.
        /// Every entry has just been revalidated by the refresh, so the journal can start over as well.
        /// </summary>
        private static void PruneAndSave<T>(JournaledCache<T> cache, ICollection<string> currentSongPaths) where T : class
        {
            // The paths passed in are usually a ConcurrentDictionary.Keys snapshot, which is list backed, so
            // Contains() is O(n) and pruning a large library becomes O(n^2). An existing HashSet is reused
            // as-is to keep the caller's equality comparer.
            var currentSongPathSet = currentSongPaths as HashSet<string> ?? new HashSet<string>(currentSongPaths);

            foreach (var levelPath in cache.Entries.Keys)
            {
                var absolutePath = GetAbsolutePath(levelPath);
                if (!currentSongPathSet.Contains(absolutePath) || (absolutePath == levelPath && IsInInstallPath(levelPath)))
                {
                    if (cache.Entries.TryRemove(levelPath, out _))
                    {
                        cache.MarkOutdated();
                    }
                }
            }

            cache.WriteSnapshotIfOutdated();
        }

        /// <summary>
        /// Records the beatmaps changed during a quick refresh in the journals instead of rewriting the
        /// whole snapshots.
        /// </summary>
        /// <returns>The number of hash and duration records written.</returns>
        internal static Task<(int hashes, int durations)> JournalPendingChangesAsync()
        {
            return Task.Run(() => (SongHashCache.AppendPendingChanges(), AudioCache.AppendPendingChanges()));
        }

        /// <summary>
        /// Stores the hash of a beatmap and records the change so it can be journaled.
        /// </summary>
        private static void StoreSongHash(string relativePath, long directoryHash, string hash)
        {
            cachedSongHashData[relativePath] = new SongHashData(directoryHash, hash);
            SongHashCache.MarkChanged(relativePath);
        }

        /// <summary>
        /// Stores the duration of a beatmap and records the change so it can be journaled.
        /// </summary>
        internal static void StoreAudioDuration(string relativePath, long directoryHash, float duration)
        {
            cachedAudioData[relativePath] = new AudioCacheData(directoryHash, duration);
            AudioCache.MarkChanged(relativePath);
        }

        /// <summary>
        /// Records that the caches no longer hold anything for a beatmap folder. The memoized directory
        /// hash is dropped as well, so that a beatmap placed in the same folder later on is not mistaken
        /// for the removed one.
        /// </summary>
        internal static void MarkSongRemoved(string customLevelPath)
        {
            DirectoryHashCache.TryRemove(customLevelPath, out _);

            if (TryGetRelativePath(customLevelPath, out var relativePath))
            {
                SongHashCache.MarkDeleted(relativePath);
                AudioCache.MarkDeleted(relativePath);
            }
        }

        internal static long GetDirectoryHash(string directory)
        {
            // Combined in a fixed order rather than XORed, because XOR cancels out when the same value
            // appears twice, so swapping the contents of two files in a folder would go unnoticed.
            long hash = 17;
            var directoryInfo = new DirectoryInfo(directory);
            foreach (var f in directoryInfo.GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                hash = hash * 31 + f.LastWriteTimeUtc.ToFileTimeUtc();
                hash = hash * 31 + f.Length;
                hash = hash * 31 + StringComparer.Ordinal.GetHashCode(f.Name);
            }

            return hash;
        }

        /// <summary>
        /// Returns the directory hash of a beatmap folder, computing it at most once per refresh so that
        /// the hash, duration and song data caches can all validate against it without extra file access.
        /// </summary>
        internal static long GetCurrentDirectoryHash(string customLevelPath)
        {
            return DirectoryHashCache.GetOrAdd(customLevelPath, GetDirectoryHash);
        }

        internal static void ClearDirectoryHashCache()
        {
            DirectoryHashCache.Clear();
        }

        private static bool GetCachedSongData(string customLevelPath, out long directoryHash, out string cachedSongHash)
        {
            directoryHash = GetCurrentDirectoryHash(customLevelPath);

            TryGetRelativePath(customLevelPath, out var relativePath);
            if (cachedSongHashData.TryGetValue(relativePath, out var cachedSong) && cachedSong.directoryHash == directoryHash)
            {
                cachedSongHash = cachedSong.songHash;
                return true;
            }

            cachedSongHash = string.Empty;
            return false;
        }

        [Obsolete("If your intent is to hash the custom level, use ComputeCustomLevelHash. Otherwise, use Collections.GetCustomLevelHash.", true)]
        public static string GetCustomLevelHash(BeatmapLevel level)
        {
            return ComputeCustomLevelHash(level);
        }

        [Obsolete("If your intent is to hash the custom level, use ComputeCustomLevelHash. Otherwise, use Collections.GetCustomLevelHash.", true)]
        public static string GetCustomLevelHash(CustomLevelFolderInfo customLevelFolderInfo, StandardLevelInfoSaveData standardLevelInfoSaveData)
        {
            return ComputeCustomLevelHash(customLevelFolderInfo, standardLevelInfoSaveData);
        }

        [Obsolete("If your intent is to hash the custom level, use ComputeCustomLevelHash. Otherwise, use Collections.GetCustomLevelHash.", true)]
        public static string GetCustomLevelHash(CustomLevelFolderInfo customLevelFolderInfo, BeatmapLevelSaveData beatmapLevelSaveData)
        {
            return ComputeCustomLevelHash(customLevelFolderInfo, beatmapLevelSaveData);
        }

        public static string ComputeCustomLevelHash(BeatmapLevel level)
        {
            var hash = string.Empty;

            if (Loader.CustomLevelLoader._loadedBeatmapSaveData.TryGetValue(level.levelID, out var loadedSaveData))
            {
                if (loadedSaveData.standardLevelInfoSaveData != null)
                {
                    hash = ComputeCustomLevelHash(loadedSaveData.customLevelFolderInfo, loadedSaveData.standardLevelInfoSaveData);
                }
                else if (loadedSaveData.beatmapLevelSaveData != null)
                {
                    hash = ComputeCustomLevelHash(loadedSaveData.customLevelFolderInfo, loadedSaveData.beatmapLevelSaveData);
                }
            }

            return hash;
        }

        public static string ComputeCustomLevelHash(CustomLevelFolderInfo customLevelFolderInfo, StandardLevelInfoSaveData standardLevelInfoSaveData)
        {
            if (GetCachedSongData(customLevelFolderInfo.folderPath, out var directoryHash, out var songHash))
            {
                return songHash;
            }

            var infoPath = Path.Combine(customLevelFolderInfo.folderPath, CustomLevelPathHelper.kStandardLevelInfoFilename);
            var files = standardLevelInfoSaveData.difficultyBeatmapSets
                .SelectMany(difficultyBeatmapSet => difficultyBeatmapSet.difficultyBeatmaps)
                .Select(difficultyBeatmap => Path.Combine(customLevelFolderInfo.folderPath, difficultyBeatmap.beatmapFilename))
                .Where(File.Exists)
                .Prepend(infoPath);

            var hash = CreateSha1FromLevelFiles(files);
            TryGetRelativePath(customLevelFolderInfo.folderPath, out var relativePath);
            StoreSongHash(relativePath, directoryHash, hash);

            return hash;
        }

        public static string ComputeCustomLevelHash(CustomLevelFolderInfo customLevelFolderInfo, BeatmapLevelSaveData beatmapLevelSaveData)
        {
            if (GetCachedSongData(customLevelFolderInfo.folderPath, out var directoryHash, out var songHash))
            {
                return songHash;
            }

            var infoPath = Path.Combine(customLevelFolderInfo.folderPath, CustomLevelPathHelper.kStandardLevelInfoFilename);
            var audioDataPath = Path.Combine(customLevelFolderInfo.folderPath, beatmapLevelSaveData.audio.audioDataFilename);
            var files = beatmapLevelSaveData.difficultyBeatmaps.SelectMany(difficultyBeatmap => new[]
            {
                Path.Combine(customLevelFolderInfo.folderPath, difficultyBeatmap.beatmapDataFilename),
                Path.Combine(customLevelFolderInfo.folderPath, difficultyBeatmap.lightshowDataFilename)
            }).Prepend(audioDataPath).Where(File.Exists).Prepend(infoPath);

            var hash = CreateSha1FromLevelFiles(files);
            TryGetRelativePath(customLevelFolderInfo.folderPath, out var relativePath);
            StoreSongHash(relativePath, directoryHash, hash);

            return hash;
        }

        public static string GetAbsolutePath(string path)
        {
            // The same relative paths are converted repeatedly (once when seeding the found song paths
            // and once per cache pruning pass), and InstallPath cannot change while the game is running,
            // so the results are memoized. Cleared on a full refresh to drop paths of removed beatmaps.
            return AbsolutePathCache.GetOrAdd(path, GetAbsolutePathUncached);
        }

        internal static void ClearPathCaches()
        {
            AbsolutePathCache.Clear();
            RelativePathCache.Clear();
        }

        private static string GetAbsolutePathUncached(string path)
        {
            path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (path.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return Path.Combine(IPA.Utilities.UnityGame.InstallPath, path.Substring(2));
            }

            return path;
        }

        public static bool TryGetRelativePath(string path, out string relativePath)
        {
            // Building the relative path allocates two Uri instances, which is expensive enough to matter
            // when it runs once per beatmap. InstallPath cannot change while the game is running, so the
            // results are memoized. Cleared on a full refresh to drop paths of removed beatmaps.
            if (RelativePathCache.TryGetValue(path, out var cached))
            {
                relativePath = cached;
                return cached != path;
            }

            var converted = TryGetRelativePathUncached(path, out relativePath);
            RelativePathCache[path] = relativePath;
            return converted;
        }

        private static bool TryGetRelativePathUncached(string path, out string relativePath)
        {
            var fromPath = IPA.Utilities.UnityGame.InstallPath;

            if (!fromPath.EndsWith(Path.DirectorySeparatorChar))
            {
                fromPath += Path.DirectorySeparatorChar;
            }

            if (!path.StartsWith(fromPath, StringComparison.Ordinal))
            {
                relativePath = path;
                return false;
            }

            var fromUri = new Uri(fromPath);
            var toUri = new Uri(path);

            relativePath = Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString());

            if (!relativePath.StartsWith(".", StringComparison.Ordinal))
            {
                relativePath = Path.Combine(".", relativePath);
            }

            relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            return true;
        }


        public static bool IsInInstallPath(string path)
        {
            var fromPath = IPA.Utilities.UnityGame.InstallPath;

            if (!fromPath.EndsWith(Path.DirectorySeparatorChar))
            {
                fromPath += Path.DirectorySeparatorChar;
            }

            return path.StartsWith(fromPath, StringComparison.Ordinal);
        }

        private static string ToHexString(byte[] bytes)
        {
            return string.Create(bytes.Length * 2, bytes, (chars, state) =>
            {
                for (var i = 0; i < state.Length; i++)
                {
                    // https://stackoverflow.com/questions/311165/how-do-you-convert-a-byte-array-to-a-hexadecimal-string-and-vice-versa/14333437#14333437
                    var b = state[i] >> 4;
                    chars[i * 2] = (char)(b < 10 ? '0' + b : 'A' - 10 + b);
                    b = state[i] & 0xF;
                    chars[i * 2 + 1] = (char)(b < 10 ? '0' + b : 'A' - 10 + b);
                }
            });
        }

        private static string CreateSha1FromLevelFiles(IEnumerable<string> files)
        {
            return CreateSha1(null, files);
        }

        /// <summary>
        /// The helper of 3.16.0, kept for mods that call it through reflection. Hashes the given bytes followed by
        /// the contents of the files, the same way 3.16.0 did.
        /// </summary>
        private static string CreateSha1FromFilesWithPrependBytes(byte[] prependBytes, IEnumerable<string> files)
        {
            return CreateSha1(prependBytes, files);
        }

        /// <summary>
        /// The helper of 3.16.0, kept for mods that call it through reflection. Returns upper case hexadecimal.
        /// </summary>
        private static string ByteToHexBitFiddle(byte[] bytes)
        {
            return ToHexString(bytes);
        }

        private static string CreateSha1(byte[]? prependBytes, IEnumerable<string> files)
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            if (prependBytes != null)
            {
                incrementalHash.AppendData(prependBytes);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(131072);
            try
            {
                foreach (var file in files)
                {
                    // Large reads go directly into our buffer, so the internal buffer size is irrelevant.
                    using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);

                    int bytesRead;
                    while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        incrementalHash.AppendData(buffer, 0, bytesRead);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return ToHexString(incrementalHash.GetHashAndReset());
        }
    }
}

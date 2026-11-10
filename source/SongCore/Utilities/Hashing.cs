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

        internal static ConcurrentDictionary<string, SongHashData> cachedSongHashData => SongHashCache.Entries;
        internal static ConcurrentDictionary<string, AudioCacheData> cachedAudioData => AudioCache.Entries;

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

        internal static Task SaveCachedSongHashesAsync(ICollection<string> currentSongPaths)
        {
            return Task.Run(() => PruneAndSave(SongHashCache, currentSongPaths));
        }

        internal static Task SaveCachedAudioDataAsync(ICollection<string> currentSongPaths)
        {
            return Task.Run(() => PruneAndSave(AudioCache, currentSongPaths));
        }

        /// <summary>
        /// Drops entries for beatmaps that are no longer present and writes a fresh snapshot. Every entry
        /// has just been revalidated by the refresh, so the journal can start over as well.
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
                    cache.Entries.TryRemove(levelPath, out _);
                }
            }

            cache.WriteSnapshot();
        }

        /// <summary>
        /// Records the beatmaps changed during a quick refresh in the journals instead of rewriting the
        /// whole snapshots.
        /// </summary>
        internal static Task JournalPendingChangesAsync()
        {
            return Task.Run(() =>
            {
                SongHashCache.AppendPendingChanges();
                AudioCache.AppendPendingChanges();
            });
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
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

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

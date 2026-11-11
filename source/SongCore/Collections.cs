using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPA.Utilities;
using SongCore.Data;
using SongCore.Utilities;
using UnityEngine;

namespace SongCore
{
    public static class Collections
    {
        private static readonly List<string> _capabilities = new List<string>();
        private static readonly List<BeatmapCharacteristicSO> _customCharacteristics = new List<BeatmapCharacteristicSO>();

        internal static readonly string DataPath = Path.Combine(UnityGame.UserDataPath, nameof(SongCore), "SongCoreExtraData.dat");
        internal static readonly JournaledCache<SongData> SongDataCache = new JournaledCache<SongData>("song data", DataPath, formatVersion: 2);
        internal static readonly ConcurrentDictionary<string, string> LevelHashDictionary = new ConcurrentDictionary<string, string>();
        internal static readonly ConcurrentDictionary<string, List<string>> HashLevelDictionary = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Song data is keyed by the beatmap folder rather than the level ID, because a level ID can change
        // between refreshes when several beatmaps share a hash. This maps the level IDs currently in use
        // back onto their folder so that the public lookups keep working.
        private static readonly ConcurrentDictionary<string, string> LevelIdToRelativePath = new ConcurrentDictionary<string, string>();

        // Number of level IDs pointing at each folder, so that removing a level does not have to scan every
        // level ID to find out whether its folder is still in use. Guarded by LevelIdLookupLock together with
        // the writes to LevelIdToRelativePath.
        private static readonly Dictionary<string, int> RelativePathReferenceCount = new Dictionary<string, int>();
        private static readonly object LevelIdLookupLock = new object();

        internal static BeatmapLevelPack? WipLevelPack;
        internal static ConcurrentDictionary<string, SongData> CustomSongsData => SongDataCache.Entries;

        public static ReadOnlyCollection<string> capabilities => _capabilities.AsReadOnly();
        public static ReadOnlyCollection<BeatmapCharacteristicSO> customCharacteristics => _customCharacteristics.AsReadOnly();

        public static bool songWithHashPresent(string hash)
        {
            return HashLevelDictionary.ContainsKey(hash);
        }

        // TODO: Replace by better naming.
        public static List<string> levelIDsForHash(string hash)
        {
            return HashLevelDictionary.TryGetValue(hash, out var songs) ? songs : new List<string>();
        }

        public static string GetCustomLevelHash(string levelID)
        {
            return LevelHashDictionary.TryGetValue(levelID, out var hash) ? hash : string.Empty;
        }

        public static SongData? GetCustomLevelSongData(string levelID)
        {
            return LevelIdToRelativePath.TryGetValue(levelID, out var relativePath) ? CustomSongsData.GetValueOrDefault(relativePath) : null;
        }

        internal static void CreateCustomLevelSongData(string levelID, long directoryHash, CustomLevelLoader.LoadedSaveData loadedSaveData)
        {
            Hashing.TryGetRelativePath(loadedSaveData.customLevelFolderInfo.folderPath, out var relativePath);
            lock (LevelIdLookupLock)
            {
                if (LevelIdToRelativePath.TryGetValue(levelID, out var previousPath))
                {
                    if (previousPath != relativePath)
                    {
                        ReleaseRelativePath(previousPath);
                        AddRelativePathReference(relativePath);
                    }
                }
                else
                {
                    AddRelativePathReference(relativePath);
                }

                LevelIdToRelativePath[levelID] = relativePath;
            }

            // Cached data is discarded when the beatmap folder has changed, so edits to an info file are
            // picked up instead of being masked by whatever was cached first. Data read by an older version
            // is read again, so that it is validated the same way as newly added beatmaps.
            if (CustomSongsData.TryGetValue(relativePath, out var existing) && existing.directoryHash == directoryHash && existing._dataVersion == SongData.CurrentDataVersion)
            {
                return;
            }

            var extraSongData = new SongData { directoryHash = directoryHash };
            extraSongData.PopulateFromLoadedSaveData(loadedSaveData);
            CustomSongsData[relativePath] = extraSongData;
            SongDataCache.MarkChanged(relativePath);
        }

        internal static void RemoveCustomLevelSongData(string levelID)
        {
            lock (LevelIdLookupLock)
            {
                if (!LevelIdToRelativePath.TryRemove(levelID, out var relativePath))
                {
                    return;
                }

                // Several level IDs can point at the same folder while a refresh is in progress, so the entry
                // is only dropped once nothing refers to it any more.
                if (!ReleaseRelativePath(relativePath))
                {
                    return;
                }

                CustomSongsData.TryRemove(relativePath, out _);
                SongDataCache.MarkDeleted(relativePath);
            }
        }

        internal static void ClearLevelIdLookup()
        {
            lock (LevelIdLookupLock)
            {
                LevelIdToRelativePath.Clear();
                RelativePathReferenceCount.Clear();
            }
        }

        private static void AddRelativePathReference(string relativePath)
        {
            RelativePathReferenceCount[relativePath] = RelativePathReferenceCount.TryGetValue(relativePath, out var count) ? count + 1 : 1;
        }

        /// <returns>Whether nothing refers to the folder any more.</returns>
        private static bool ReleaseRelativePath(string relativePath)
        {
            if (!RelativePathReferenceCount.TryGetValue(relativePath, out var count) || count <= 1)
            {
                RelativePathReferenceCount.Remove(relativePath);
                return true;
            }

            RelativePathReferenceCount[relativePath] = count - 1;
            return false;
        }

        public static SongData.DifficultyData? GetCustomLevelSongDifficultyData(BeatmapKey beatmapKey)
        {
            SongData? songData = null;

            if (beatmapKey.levelId.StartsWith(CustomLevelLoader.kCustomLevelPrefixId, StringComparison.Ordinal))
            {
                // TODO: Will be null in the editor due to levelID being "custom_level_CustomLevel".
                songData = GetCustomLevelSongData(beatmapKey.levelId);
            }

            var diffData = songData?._difficulties.FirstOrDefault(x =>
                x._difficulty == beatmapKey.difficulty && (x._beatmapCharacteristicName == beatmapKey.beatmapCharacteristic.characteristicNameLocalizationKey ||
                                                           x._beatmapCharacteristicName == beatmapKey.beatmapCharacteristic.serializedName));

            return diffData;
        }

        internal static Task LoadCachedSongDataAsync()
        {
            return Task.Run(() => SongDataCache.Load((relativePath, value) => Hashing.IsFolderUnchangedInternal(relativePath, value?.directoryHash)));
        }

        /// <summary>
        /// Drops song data for beatmaps that are no longer present and writes a fresh snapshot if anything changed.
        /// </summary>
        internal static Task SaveCachedSongDataAsync()
        {
            return Task.Run(() =>
            {
                HashSet<string> currentPaths;
                lock (LevelIdLookupLock)
                {
                    currentPaths = new HashSet<string>(RelativePathReferenceCount.Keys);
                }

                foreach (var relativePath in CustomSongsData.Keys)
                {
                    if (!currentPaths.Contains(relativePath) && CustomSongsData.TryRemove(relativePath, out _))
                    {
                        SongDataCache.MarkOutdated();
                    }
                }

                SongDataCache.WriteSnapshotIfOutdated();
            });
        }

        /// <summary>
        /// Records the song data changed during a quick refresh in the journal instead of rewriting the
        /// whole snapshot.
        /// </summary>
        /// <returns>The number of records written.</returns>
        internal static Task<int> JournalChangedSongDataAsync()
        {
            return Task.Run(SongDataCache.AppendPendingChanges);
        }

        public static BeatmapCharacteristicSO? RegisterCustomCharacteristic(Sprite icon, string characteristicName, string hintText, string serializedName, string compoundIdPartName,
            bool requires360Movement = false, bool containsRotationEvents = false, int sortingOrder = 99)
        {
            var newChar = ScriptableObject.CreateInstance<BeatmapCharacteristicSO>();

            newChar._icon = icon;
            newChar._descriptionLocalizationKey = hintText;
            newChar._serializedName = serializedName;
            newChar._characteristicNameLocalizationKey = characteristicName;
            newChar._compoundIdPartName = compoundIdPartName;
            newChar._requires360Movement = requires360Movement;
            newChar._containsRotationEvents = containsRotationEvents;
            newChar._sortingOrder = sortingOrder;

            newChar.name = serializedName + "BeatmapCharacteristic";

            if (_customCharacteristics.All(x => x.serializedName != newChar.serializedName))
            {
                _customCharacteristics.Add(newChar);
                return newChar;
            }

            return null;
        }

        public static SeparateSongFolder AddSeparateSongFolder(string name, string folderPath, FolderLevelPack pack, Sprite? image = null, bool wip = false, bool cachezips = false)
        {
            UI.BasicUI.GetIcons();
            if (!Directory.Exists(folderPath))
            {
                try
                {
                    Directory.CreateDirectory(folderPath);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"Failed to make folder for: {name}");
                    Plugin.Log.Error(ex);
                }
            }

            var entry = new SongFolderEntry(name, folderPath, pack, "", wip, cachezips);
            var separateSongFolder = new ModSeparateSongFolder(entry, image == null ? UI.BasicUI.FolderIcon! : image);

            Loader.SeparateSongFolders.Add(separateSongFolder);
            return separateSongFolder;
        }

        public static void RegisterCapability(string capability)
        {
            if (!_capabilities.Contains(capability))
            {
                _capabilities.Add(capability);
            }
        }

        public static void DeregisterCapability(string capability)
        {
            _capabilities.Remove(capability);
        }
    }
}

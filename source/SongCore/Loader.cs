using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeatmapDataLoaderVersion4;
using BeatmapLevelSaveDataVersion4;
using BeatmapSaveDataVersion4;
using BeatSaber.Destinations;
using BeatSaberMarkupLanguage.Settings;
using BGLib.JsonExtension;
using IPA.Utilities;
using IPA.Utilities.Async;
using Newtonsoft.Json;
using SongCore.Data;
using SongCore.OverrideClasses;
using SongCore.UI;
using SongCore.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;

namespace SongCore
{
    public class Loader : IInitializable, IDisposable, ITickable
    {
        private readonly GameScenesManager _gameScenesManager;
        private readonly LevelFilteringNavigationController _levelFilteringNavigationController;
        private readonly LevelPackDetailViewController _levelPackDetailViewController;
        private readonly BeatmapLevelsModel _beatmapLevelsModel;
        private readonly CustomLevelLoader _customLevelLoader;
        private readonly SpriteAsyncLoader _spriteAsyncLoader;
        private readonly BeatmapCharacteristicCollection _beatmapCharacteristicCollection;
        private readonly ProgressBar _progressBar;
        private readonly BSMLSettings _bsmlSettings;
        private readonly PluginConfig _config;
        private readonly SettingsController _settingsController;
        private readonly string _customWIPPath;
        private readonly string _customLevelsPath;

        private Task? _loadingTask;
        private CancellationTokenSource _loadingTaskCancellationTokenSource = new();

        // Refreshes requested while a scene transition is in progress are deferred until it has finished,
        // since updating the level selection UI of a menu that is being dismissed breaks its navigation.
        private bool _isInSceneTransition;
        private bool? _pendingFullRefresh;

        // Set when a beatmap is removed, so that the next level pack refresh drops the game's caches.
        private volatile bool _levelsRemovedSinceCacheClear;

        private Loader(GameScenesManager gameScenesManager, LevelFilteringNavigationController levelFilteringNavigationController, LevelPackDetailViewController levelPackDetailViewController, BeatmapLevelsModel beatmapLevelsModel, CustomLevelLoader customLevelLoader, SpriteAsyncLoader spriteAsyncLoader, BeatmapCharacteristicCollection beatmapCharacteristicCollection, ProgressBar progressBar, PluginConfig config, SettingsController settingsController, BSMLSettings bsmlSettings)

        {
            _gameScenesManager = gameScenesManager;
            _levelFilteringNavigationController = levelFilteringNavigationController;
            _levelPackDetailViewController = levelPackDetailViewController;
            _beatmapLevelsModel = beatmapLevelsModel;
            _customLevelLoader = customLevelLoader;
            _spriteAsyncLoader = spriteAsyncLoader;
            _beatmapCharacteristicCollection = beatmapCharacteristicCollection;
            _progressBar = progressBar;
            _bsmlSettings = bsmlSettings;
            _config = config;
            _settingsController = settingsController;
            // Path.GetFullPath is needed to normalize directory separators.
            _customWIPPath = Path.GetFullPath(Path.Combine(Application.dataPath, "CustomWIPLevels"));
            _customLevelsPath = Path.GetFullPath(CustomLevelPathHelper.customLevelsDirectoryPath);
            Instance = this;
        }

        // Actions for loading and refreshing beatmaps
        public static event Action<Loader>? LoadingStartedEvent;
        public static event Action<Loader, ConcurrentDictionary<string, BeatmapLevel>>? SongsLoadedEvent;
        public static event Action? OnLevelPacksRefreshed;

        private static bool _duplicatesReported;

        // Relative path prefix shared by beatmaps in the regular custom levels folder, trimmed when logging duplicates.
        private static readonly string CommonLevelPathPrefix = Path.Combine(".", "Beat Saber_Data", CustomLevelPathHelper.kCustomLevelsDirectoryName) + Path.DirectorySeparatorChar;
        // Temp collection to avoid concurrency issues with base game dictionary. Also used to store levels on internal restart.
        private static ConcurrentDictionary<string, CustomLevelLoader.LoadedSaveData> LoadedBeatmapSaveData = new ConcurrentDictionary<string, CustomLevelLoader.LoadedSaveData>();
        public static ConcurrentDictionary<string, BeatmapLevel> CustomLevels = new ConcurrentDictionary<string, BeatmapLevel>();
        public static ConcurrentDictionary<string, BeatmapLevel> CustomWIPLevels = new ConcurrentDictionary<string, BeatmapLevel>();
        public static ConcurrentDictionary<string, BeatmapLevel> CachedWIPLevels = new ConcurrentDictionary<string, BeatmapLevel>();
        public static readonly List<SeparateSongFolder> SeparateSongFolders = new List<SeparateSongFolder>();
        public static Sprite defaultCoverImage = null!;
        public static Loader Instance = null!;

        public static SongCoreCustomBeatmapLevelPack? CustomLevelsPack { get; private set; }
        public static SongCoreCustomBeatmapLevelPack? WIPLevelsPack { get; private set; }
        public static SongCoreCustomBeatmapLevelPack? CachedWIPLevelsPack { get; private set; }
        public static SongCoreBeatmapLevelsRepository? CustomLevelsRepository { get; private set; }
        public static bool AreSongsLoaded { get; private set; }
        public static bool AreSongsLoading { get; private set; }
        public static float LoadingProgress { get; private set; }
        public static BeatmapLevelsModel BeatmapLevelsModelSO { get; private set; } = null!;
        public static CustomLevelLoader CustomLevelLoader { get; private set; } = null!;
        public static SpriteAsyncLoader cachedMediaAsyncLoaderSO { get; private set; } = null!;
        public static BeatmapCharacteristicCollection beatmapCharacteristicCollection { get; private set; } = null!;

        public void Initialize()
        {
            _gameScenesManager.transitionDidFinishEvent += HandleSceneTransitionDidFinish;
            // BSML might fail to find the resource if done in a patched method.
            _bsmlSettings.AddSettingsMenu(nameof(SongCore), "SongCore.UI.settings.bsml", _settingsController);

            SongsLoadedEvent += HandleSongsLoaded;
        }

        public void Dispose()
        {
            _bsmlSettings.RemoveSettingsMenu(_settingsController);

            SceneManager.activeSceneChanged -= HandleActiveSceneChanged;

            _gameScenesManager.transitionDidStartEvent -= HandleSceneTransitionDidStart;
            _gameScenesManager.transitionDidFinishEvent -= HandleSceneTransitionDidFinishAfterInit;

            SongsLoadedEvent -= HandleSongsLoaded;
        }

        /// <summary>
        /// Refresh songs on "R" key, full refresh on "Ctrl"+"R"
        /// </summary>
        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.R))
            {
                RefreshSongs(Input.GetKey(KeyCode.LeftControl));
            }

            if (Input.GetKeyDown(KeyCode.X) && Input.GetKey(KeyCode.LeftControl) && _loadingTask != null)
            {
                CancelSongLoading();
            }
        }

        private async void HandleSceneTransitionDidFinish(GameScenesManager.SceneTransitionType sceneTransitionType, ScenesTransitionSetupDataSO scenesTransitionSetupData, DiContainer container)
        {
            _gameScenesManager.transitionDidFinishEvent -= HandleSceneTransitionDidFinish;

            // Ensures that the static references are still valid Unity objects.
            // They'll be destroyed on internal restart.
            BeatmapLevelsModelSO = _beatmapLevelsModel;
            CustomLevelLoader = _customLevelLoader;
            cachedMediaAsyncLoaderSO = _spriteAsyncLoader;
            defaultCoverImage = _levelPackDetailViewController._defaultCoverSprite;
            beatmapCharacteristicCollection = _beatmapCharacteristicCollection;

            if (Hashing.cachedSongHashData.IsEmpty)
            {
                // All caches are loaded before the first refresh. Loading also merges the journals, and letting
                // that land after beatmaps have started loading would throw away everything loaded so far.
                await Task.WhenAll(Hashing.LoadCachedSongHashesAsync(), Hashing.LoadCachedAudioDataAsync(), Collections.LoadCachedSongDataAsync());
                RefreshSongs();
            }
            else
            {
                RefreshLevelPacks();
            }

            SceneManager.activeSceneChanged += HandleActiveSceneChanged;

            _gameScenesManager.transitionDidStartEvent += HandleSceneTransitionDidStart;
            _gameScenesManager.transitionDidFinishEvent += HandleSceneTransitionDidFinishAfterInit;
        }

        private void HandleSongsLoaded(Loader loader, ConcurrentDictionary<string, BeatmapLevel> customLevels)
        {
            // Reported once per launch. This runs on the main thread and walks the whole hash cache, so repeating
            // it after every refresh, including the ones triggered by downloading a song, is not worth it.
            if (_duplicatesReported)
            {
                return;
            }

            _duplicatesReported = true;

            // Both collections must use the same comparer as HashLevelDictionary, whose keys are the
            // hashes being looked up here. Hash casing is not guaranteed to be consistent between
            // HashLevelDictionary and the hash cache, so an ordinal comparison would miss entries.
            var duplicateHashes = new HashSet<string>(Collections.HashLevelDictionary.Where(x => x.Value.Count > 1).Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
            if (duplicateHashes.Count == 0)
            {
                return;
            }

            // This runs on the main thread. Grouping the hash cache once avoids scanning all of it again for
            // every duplicate, which is O(n*m) and stalls the menu on large libraries.
            var pathsByHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var cachedSong in Hashing.cachedSongHashData)
            {
                if (cachedSong.Value.songHash == null || !duplicateHashes.Contains(cachedSong.Value.songHash))
                {
                    continue;
                }

                if (!pathsByHash.TryGetValue(cachedSong.Value.songHash, out var cachedPaths))
                {
                    cachedPaths = new List<string>();
                    pathsByHash[cachedSong.Value.songHash] = cachedPaths;
                }

                cachedPaths.Add(cachedSong.Key);
            }

            var duplicateCount = 0;
            var builder = new StringBuilder();
            foreach (var (hash, levels) in Collections.HashLevelDictionary)
            {
                if (levels.Count <= 1)
                {
                    continue;
                }

                duplicateCount++;

                if (!pathsByHash.TryGetValue(hash, out var levelPaths))
                {
                    continue;
                }

                if (_config.VerboseDuplicateLogging)
                {
                    Plugin.Log.Notice("Found duplicates:");
                    foreach (var levelPath in levelPaths)
                    {
                        Plugin.Log.Notice($"  {levelPath}");
                    }

                    continue;
                }

                // One line per group of duplicates. Logging every path separately produces enough messages on
                // large libraries that the log subsystem starts dropping them.
                builder.Clear();
                for (var i = 0; i < levelPaths.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(" | ");
                    }

                    // The common parent is dropped to keep the line readable. Paths outside of the regular
                    // custom levels folder are kept as-is so they remain identifiable.
                    var levelPath = levelPaths[i];
                    builder.Append(levelPath.StartsWith(CommonLevelPathPrefix, StringComparison.OrdinalIgnoreCase)
                        ? levelPath.Substring(CommonLevelPathPrefix.Length)
                        : levelPath);
                }

                Plugin.Log.Notice($"  {builder}");
            }

            if (duplicateCount > 0)
            {
                Plugin.Log.Notice($"Found {duplicateCount} duplicate hashes.");
            }
        }

        private void HandleSceneTransitionDidStart(GameScenesManager.SceneTransitionType sceneTransitionType, float duration)
        {
            _isInSceneTransition = true;
            CancelSongLoading();
        }

        private void HandleSceneTransitionDidFinishAfterInit(GameScenesManager.SceneTransitionType sceneTransitionType, ScenesTransitionSetupDataSO scenesTransitionSetupData, DiContainer container)
        {
            _isInSceneTransition = false;
            RunPendingRefresh();
        }

        /// <summary>
        /// Runs a refresh that was requested while it could not run. A refresh deferred on the way into a level is
        /// kept until the transition back to the menu has finished.
        /// </summary>
        private void RunPendingRefresh()
        {
            if (_pendingFullRefresh is { } fullRefresh && !AreSongsLoading && !_isInSceneTransition && SceneManager.GetActiveScene().name != SceneNames.kGameCoreSceneName)
            {
                _pendingFullRefresh = null;
                RefreshSongs(fullRefresh);
            }
        }

        private void CancelSongLoading()
        {
            if (AreSongsLoading)
            {
                _loadingTaskCancellationTokenSource.Cancel();
                AreSongsLoading = false;
                LoadingProgress = 0;
                _progressBar.ShowMessage("Loading cancelled\n<size=80%>Press R to resume</size>", false);
            }
        }

        private void HandleActiveSceneChanged(Scene previousScene, Scene nextScene)
        {
            if (_loadingTaskCancellationTokenSource.IsCancellationRequested && nextScene.name == "MainMenu")
            {
                Plugin.Log.Notice("Song loading was cancelled. Resuming...");
                RefreshSongs(false);
            }
        }

        /// <summary>
        /// This function will add/remove Level Packs from the Custom Levels tab if applicable
        /// </summary>
        public async void RefreshLevelPacks()
        {
            await RefreshLevelPacksAsync(true);
        }

        /// <summary>
        /// Implementation of <see cref="RefreshLevelPacks"/> that can be awaited internally. Loading is
        /// only reported as finished once this has run to completion, so that no work is left running in
        /// the background afterwards. The main thread part below is heavy on large libraries, and letting
        /// it run at an arbitrary point could freeze the game when it lands on a scene transition.
        /// </summary>
        /// <param name="clearCaches">
        /// Whether the cached covers and beatmap data of the game are dropped. They only become stale when a
        /// beatmap was removed, since a beatmap placed in the same folder or under the same level ID afterwards
        /// would otherwise show what was cached for the removed one. Keeping them when beatmaps were only added
        /// avoids loading every cover and preview again.
        /// </param>
        private async Task RefreshLevelPacksAsync(bool clearCaches)
        {
            CustomLevelsPack?.UpdateBeatmapLevels([.. CustomLevels.Values]);
            WIPLevelsPack?.UpdateBeatmapLevels([.. CustomWIPLevels.Values]);
            CachedWIPLevelsPack?.UpdateBeatmapLevels([.. CachedWIPLevels.Values]);

            if (CachedWIPLevelsPack != null && CustomLevelsRepository != null)
            {
                if (!CachedWIPLevels.IsEmpty)
                {
                    CustomLevelsRepository.AddLevelPack(CachedWIPLevelsPack);
                }
                else
                {
                    CustomLevelsRepository.RemoveLevelPack(CachedWIPLevelsPack);
                }
            }

            foreach (var folderEntry in SeparateSongFolders)
            {
                if (folderEntry.SongFolderEntry.Pack == FolderLevelPack.NewPack)
                {
                    folderEntry.LevelPack.UpdateBeatmapLevels([.. folderEntry.Levels.Values]);
                    if (CustomLevelsRepository != null && (!folderEntry.Levels.IsEmpty || folderEntry is ModSeparateSongFolder { AlwaysShow: true }))
                    {
                        CustomLevelsRepository.AddLevelPack(folderEntry.LevelPack);
                    }
                }
            }

            await UnityGame.SwitchToMainThreadAsync();

            // Written back before the level packs are rebuilt, so that anything reacting to the rebuild (for
            // example BetterSongList) already finds the save data of newly loaded beatmaps. This stays after
            // the switch to the main thread, since the loader's dictionary is read from the main thread.
            // ClearLoadedBeatmapLevelsCaches only clears media and level caches, not the save data.
            foreach (var (levelID, loadedSaveData) in LoadedBeatmapSaveData)
            {
                _customLevelLoader._loadedBeatmapSaveData[levelID] = loadedSaveData;
            }

            LoadedBeatmapSaveData.Clear();

            if (clearCaches || _levelsRemovedSinceCacheClear)
            {
                _levelsRemovedSinceCacheClear = false;
                _beatmapLevelsModel.ClearLoadedBeatmapLevelsCaches();
            }

            _beatmapLevelsModel._customLevelsRepository = CustomLevelsRepository;
            _beatmapLevelsModel.LoadAllBeatmapLevelPacks();

            if (!_loadingTaskCancellationTokenSource.IsCancellationRequested && _levelFilteringNavigationController.isActiveAndEnabled)
            {
                _levelFilteringNavigationController.UpdateCustomSongs();
            }

            OnLevelPacksRefreshed?.Invoke();
        }

        public void RefreshSongs(bool fullRefresh = true)
        {
            if (SceneManager.GetActiveScene().name == SceneNames.kGameCoreSceneName)
            {
                return;
            }

            // Requests made while songs are loading or while a scene transition is in progress are merged into
            // one refresh that runs afterwards. Dropping them would leave beatmaps downloaded during a refresh
            // missing until the next one.
            if (AreSongsLoading || _isInSceneTransition)
            {
                _pendingFullRefresh = _pendingFullRefresh == true || fullRefresh;
                return;
            }

            Plugin.Log.Info(fullRefresh ? "Starting full song refresh" : "Starting song refresh");
            AreSongsLoaded = false;
            AreSongsLoading = true;
            LoadingProgress = 0;
            _loadingTaskCancellationTokenSource = new CancellationTokenSource();
            if (LoadingStartedEvent != null)
            {
                try
                {
                    LoadingStartedEvent(this);
                }
                catch (Exception e)
                {
                    Plugin.Log.Error("Some plugin is throwing exception from the LoadingStartedEvent!");
                    Plugin.Log.Error(e);
                }
            }

            RetrieveAllSongs(fullRefresh);
        }

        private async void RetrieveAllSongs(bool fullRefresh)
        {
            var stopwatch = new Stopwatch();

            #region ClearAllDictionaries

            // Clear all beatmap dictionaries on full refresh
            if (fullRefresh)
            {
                CustomLevels.Clear();
                CustomWIPLevels.Clear();
                CachedWIPLevels.Clear();
                Collections.LevelHashDictionary.Clear();
                Collections.HashLevelDictionary.Clear();
                Collections.ClearLevelIdLookup();
                Hashing.ClearPathCaches();
                Hashing.ClearDirectoryHashCache();
                foreach (var folder in SeparateSongFolders)
                {
                    folder.Levels.Clear();
                }
            }

            #endregion

            // Only used to prune the caches after a full refresh. A quick refresh journals its changes instead,
            // so it does not need to know about every beatmap.
            var foundSongPaths = new ConcurrentDictionary<string, bool>();

            var job = async () =>
            {
                #region AddCustomBeatmaps

                try
                {
                    #region DirectorySetup

                    if (!Directory.Exists(_customLevelsPath))
                    {
                        Directory.CreateDirectory(_customLevelsPath);
                    }

                    if (!Directory.Exists(_customWIPPath))
                    {
                        Directory.CreateDirectory(_customWIPPath);
                    }

                    #endregion

                    #region CacheZipWIPs

                    // Get zip files in CustomWIPLevels and extract them to Cache folder
                    if (fullRefresh)
                    {
                        try
                        {
                            var cachePath = Path.Combine(_customWIPPath, "Cache");
                            CacheZIPs(cachePath, _customWIPPath);

                            var cacheFolders = Directory.EnumerateDirectories(cachePath);
                            await LoadCachedZIPs(cacheFolders, fullRefresh, CachedWIPLevels);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Error("Failed to load cached WIP levels: ");
                            Plugin.Log.Error(ex);
                        }
                    }

                    #endregion

                    #region CacheSeparateZIPs

                    if (fullRefresh)
                    {
                        foreach (var songFolder in SeparateSongFolders)
                        {
                            if (songFolder.SongFolderEntry.CacheZIPs && songFolder.CacheFolder != null)
                            {
                                var cacheFolder = songFolder.CacheFolder;
                                try
                                {
                                    CacheZIPs(cacheFolder.SongFolderEntry.Path, songFolder.SongFolderEntry.Path);
                                }
                                catch (Exception ex)
                                {
                                    Plugin.Log.Error("Failed to load cached WIP levels:");
                                    Plugin.Log.Error(ex);
                                }
                            }
                        }
                    }

                    #endregion

                    stopwatch.Start();

                    #region LoadCustomLevels

                    // Get Levels from CustomLevels and CustomWIPLevels folders
                    var songFolders = new DirectoryInfo(_customLevelsPath).EnumerateDirectories()
                        .Concat(new DirectoryInfo(_customWIPPath).EnumerateDirectories())
                        .Where(d => !CustomLevelPathHelper.IsHiddenDirectory(d))
                        .Select(d => d.FullName)
                        .ToArray();
                    var songFoldersCount = songFolders.Length;
                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = LoadingParallelism.Value,
                        CancellationToken = _loadingTaskCancellationTokenSource.Token
                    };
                    var processedSongsCount = 0;

                    // Clear removed songs from loaded data, in case they were removed manually.
                    if (_customLevelLoader._loadedBeatmapSaveData.Count > 0)
                    {
                        // Beatmaps extracted from zips live in subfolders of the WIP cache folder, which are not part
                        // of songFolders. Without them, every cached WIP level was removed on a quick refresh.
                        var cacheFolder = new DirectoryInfo(Path.Combine(_customWIPPath, "Cache"));
                        var cachedFolders = cacheFolder.Exists
                            ? cacheFolder.EnumerateDirectories().Where(d => !CustomLevelPathHelper.IsHiddenDirectory(d)).Select(d => d.FullName)
                            : Enumerable.Empty<string>();
                        var folders = songFolders
                            .Concat(cachedFolders)
                            .Concat(SeparateSongFolders
                                .Select(f => Path.GetFullPath(f.SongFolderEntry.Path))
                                .Select(p => new DirectoryInfo(p))
                                .Where(d => d.Exists)
                                .SelectMany(d => d.EnumerateDirectories()
                                    .Where(d => !CustomLevelPathHelper.IsHiddenDirectory(d))
                                    .Select(d => d.FullName)))
                            // Case insensitive because the casing of configured paths (e.g. folders.xml) does not
                            // have to match the casing used when the beatmap was loaded.
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        // Need to make a copy of the loaded save data since we might modify the iterated collection.
                        foreach (var loadedSaveData in _customLevelLoader._loadedBeatmapSaveData.Values.ToArray())
                        {
                            if (!folders.Contains(loadedSaveData.customLevelFolderInfo.folderPath))
                            {
                                Plugin.Log.Warn($"Removing {loadedSaveData.customLevelFolderInfo.folderPath} from loaded levels");
                                DeleteSingleSong(loadedSaveData.customLevelFolderInfo.folderPath, false);
                            }
                        }

                        // The loaded save data used to be copied here and written back in full after a quick refresh.
                        // The game only fills CustomLevelLoader._loadedBeatmapSaveData when it loads custom levels
                        // itself, which SongCore prevents, and ClearLoadedBeatmapLevelsCaches does not touch it
                        // either (1.44.1). Writing back only the beatmaps loaded by this refresh is therefore enough.
                    }

                    Parallel.ForEach(songFolders, parallelOptions, folder =>
                    {
                        // Skip beatmaps that are already loaded before hitting the file system. Without this,
                        // a quick refresh still enumerates every single song folder. 'folder' equals the
                        // songPath used as the dictionary key, since the info file is always located in the
                        // root of the beatmap folder.
                        if (!fullRefresh && (CustomLevels.ContainsKey(folder) || CustomWIPLevels.ContainsKey(folder)))
                        {
                            LoadingProgress = (float)Interlocked.Increment(ref processedSongsCount) / songFoldersCount;
                            return;
                        }

                        string[] results;
                        try
                        {
                            results = Directory.GetFiles(folder, CustomLevelPathHelper.kStandardLevelInfoFilename);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Warn($"Skipping missing or corrupt folder: '{folder}'");
                            Plugin.Log.Warn(ex);
                            return;
                        }

                        if (results.Length == 0)
                        {
                            Plugin.Log.Warn($"Folder: '{folder}' is missing {CustomLevelPathHelper.kStandardLevelInfoFilename} file!");
                            return;
                        }

                        foreach (var result in results)
                        {
                            try
                            {
                                var songPath = Path.GetDirectoryName(result)!;
                                if (Directory.GetParent(songPath)?.Name == "Backups")
                                {
                                    continue;
                                }

                                if (!fullRefresh && (CustomLevels.ContainsKey(songPath) || CustomWIPLevels.ContainsKey(songPath)))
                                {
                                    continue;
                                }

                                var wip = songPath.Contains("CustomWIPLevels");
                                var customLevel = LoadCustomLevel(songPath);
                                if (!customLevel.HasValue)
                                {
                                    Plugin.Log.Error($"Failed to load custom level: {folder}");
                                    continue;
                                }

                                var (_, level) = customLevel.Value;
                                if (!wip)
                                {
                                    CustomLevels[songPath] = level;
                                }
                                else
                                {
                                    CustomWIPLevels[songPath] = level;
                                }

                                foundSongPaths.TryAdd(songPath, false);
                            }
                            catch (Exception e)
                            {
                                Plugin.Log.Error($"Failed to load song folder: {result}");
                                Plugin.Log.Error(e);
                            }
                        }

                        LoadingProgress = (float)Interlocked.Increment(ref processedSongsCount) / songFoldersCount;
                    });

                    #endregion

                    #region LoadSeparateFolders

                    // Load beatmaps in separate song folders (created in folders.xml or by other mods)
                    // Assign beatmaps to their respective pack (custom levels, wip levels, or separate)
                    await UnityMainThreadTaskScheduler.Factory.StartNew(() => _progressBar.ShowMessage($"Loading {SeparateSongFolders.Count} separate folders", true));
                    foreach (var entry in SeparateSongFolders)
                    {
                        try
                        {
                            if (!Directory.Exists(entry.SongFolderEntry.Path))
                            {
                                continue;
                            }

                            var entryFolders = Directory.GetDirectories(entry.SongFolderEntry.Path);

                            float count = 0;
                            foreach (var folder in entryFolders)
                            {
                                count++;

                                // Skip beatmaps that are already loaded before hitting the file system, like for the
                                // regular folders. The check after reading the folder is kept for paths that are not
                                // spelled the same way as the loaded beatmap (e.g. separators from folders.xml).
                                if (!fullRefresh && IsLoadedInSeparateFolderPack(entry, folder))
                                {
                                    continue;
                                }

                                // Search for an info.dat in the beatmap folder
                                string[] results;
                                try
                                {
                                    results = Directory.GetFiles(folder, CustomLevelPathHelper.kStandardLevelInfoFilename);
                                }
                                catch (Exception ex)
                                {
                                    Plugin.Log.Warn($"Skipping missing or corrupt folder: '{folder}'");
                                    Plugin.Log.Warn(ex);
                                    continue;
                                }

                                if (results.Length == 0)
                                {
                                    Plugin.Log.Warn($"Folder: '{folder}' is missing {CustomLevelPathHelper.kStandardLevelInfoFilename} file!");
                                    continue;
                                }

                                foreach (var result in results)
                                {
                                    try
                                    {
                                        // On quick refresh: Check if the beatmap directory is already present in the respective beatmap dictionary
                                        // If it is already present on a non full refresh, it will be ignored (changes to the beatmap will not be applied)
                                        var songPath = Path.GetDirectoryName(result)!;
                                        if (!fullRefresh && IsLoadedInSeparateFolderPack(entry, songPath))
                                        {
                                            continue;
                                        }

                                        if (entry.SongFolderEntry.Pack == FolderLevelPack.CustomLevels || entry.SongFolderEntry is { Pack: FolderLevelPack.NewPack, WIP: false })
                                        {
                                            if (AssignBeatmapToSeparateFolder(CustomLevels, songPath, entry.Levels))
                                            {
                                                continue;
                                            }

                                            if (AssignBeatmapToSeparateFolder(CustomWIPLevels, songPath, entry.Levels))
                                            {
                                                continue;
                                            }

                                            if (AssignBeatmapToSeparateFolder(CachedWIPLevels, songPath, entry.Levels))
                                            {
                                                continue;
                                            }
                                        }

                                        var customLevel = LoadCustomLevel(songPath, entry.SongFolderEntry);
                                        if (!customLevel.HasValue)
                                        {
                                            Plugin.Log.Error($"Failed to load custom level: {folder}");
                                        }
                                        else
                                        {
                                            var (_, level) = customLevel.Value;
                                            entry.Levels[songPath] = level;
                                            foundSongPaths.TryAdd(songPath, false);
                                        }

                                        LoadingProgress = count / entryFolders.Length;
                                    }
                                    catch (Exception e)
                                    {
                                        Plugin.Log.Error($"Failed to load song folder: {result}");
                                        Plugin.Log.Error(e);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Error($"Failed to load separate folder {entry.SongFolderEntry.Name}");
                            Plugin.Log.Error(ex);
                        }
                    }

                    #endregion

                    _loadingTaskCancellationTokenSource.Token.ThrowIfCancellationRequested();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Plugin.Log.Error("RetrieveAllSongs failed:");
                    Plugin.Log.Error(e);
                }

                #endregion
            };

            var finish = async () =>
            {
                #region CountBeatmapsAndUpdateLevelPacks

                stopwatch.Stop();
                var songCountWSF = CustomLevels.Count + CustomWIPLevels.Count;
                var songCount = songCountWSF + SeparateSongFolders.Sum(f => f.Levels.Count);

                var folderCount = songCount - songCountWSF;
                var songOrSongs = songCount == 1 ? "song" : "songs";
                var folderOrFolders = folderCount == 1 ? "folder" : "folders";
                Plugin.Log.Info($"Loaded {songCount} new {songOrSongs} ({songCountWSF}) in CustomLevels | {folderCount} in separate {folderOrFolders}) in {stopwatch.Elapsed.TotalSeconds} seconds");
                try
                {
                    #region AddSeparateFolderBeatmapsToRespectivePacks

                    foreach (var folderEntry in SeparateSongFolders)
                    {
                        // Merged in place. Rebuilding the whole dictionary for every separate folder costs as much
                        // as the library is large, while only the beatmaps of the folder have to be added.
                        var target = folderEntry.SongFolderEntry.Pack switch
                        {
                            FolderLevelPack.CustomLevels => CustomLevels,
                            FolderLevelPack.CustomWIPLevels => CustomWIPLevels,
                            FolderLevelPack.CachedWIPLevels => CachedWIPLevels,
                            _ => null
                        };

                        if (target != null)
                        {
                            foreach (var (songPath, level) in folderEntry.Levels)
                            {
                                target.TryAdd(songPath, level);
                            }
                        }
                    }

                    #endregion

                    #region CreateLevelPacks

                    CustomLevelsRepository ??= SongCoreBeatmapLevelsRepository.CreateNew();
                    if (CustomLevelsRepository.beatmapLevelPacks.Count == 0)
                    {
                        // Create level collections and level packs
                        // Add level packs to the custom levels pack collection

                        // This creates unity sprites, so it needs to be on the main thread
                        await UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                        {
                            CustomLevelsPack = new SongCoreCustomBeatmapLevelPack(CustomLevelLoader.kCustomLevelPackPrefixId + CustomLevelPathHelper.kCustomLevelsDirectoryName, "Custom Levels", defaultCoverImage, CustomLevels.Values.ToArray());
                            // The icons are loaded when the plugin is enabled, before any song is loaded.
                            WIPLevelsPack = new SongCoreCustomBeatmapLevelPack(CustomLevelLoader.kCustomLevelPackPrefixId + "CustomWIPLevels", "WIP Levels", UI.BasicUI.WIPIcon!, CustomWIPLevels.Values.ToArray());
                            CachedWIPLevelsPack = new SongCoreCustomBeatmapLevelPack(CustomLevelLoader.kCustomLevelPackPrefixId + "CachedWIPLevels", "Cached WIP Levels", UI.BasicUI.WIPIcon!,
                                CachedWIPLevels.Values.ToArray());
                        });

                        // Assigned on the main thread right above, which has completed at this point.
                        CustomLevelsRepository.ClearLevelPacks();
                        CustomLevelsRepository.AddLevelPack(CustomLevelsPack!);
                        CustomLevelsRepository.AddLevelPack(WIPLevelsPack!);
                        CustomLevelsRepository.AddLevelPack(CachedWIPLevelsPack!);
                    }

                    #endregion

                    // Caches are written before the level packs are reflected, so that nothing is left
                    // running in the background once loading is reported as finished. A failure here must
                    // not keep the level packs from being refreshed.
                    try
                    {
                        if (fullRefresh)
                        {
                            // Every entry has just been revalidated, so a fresh snapshot is written and the
                            // journals start over.
                            await Task.WhenAll(Hashing.SaveCachedSongHashesAsync(foundSongPaths.Keys), Hashing.SaveCachedAudioDataAsync(foundSongPaths.Keys), Collections.SaveCachedSongDataAsync());
                        }
                        else
                        {
                            // Only a handful of beatmaps changed, so appending to the journals avoids
                            // rewriting tens of megabytes of unchanged data.
                            var journalStopwatch = Stopwatch.StartNew();
                            var cacheJournalTask = Hashing.JournalPendingChangesAsync();
                            var songDataJournalTask = Collections.JournalChangedSongDataAsync();
                            await Task.WhenAll(cacheJournalTask, songDataJournalTask);

                            // One line for all caches. The three are written together, so separate lines only add noise.
                            var (hashes, durations) = cacheJournalTask.Result;
                            var songData = songDataJournalTask.Result;
                            if (hashes + durations + songData > 0)
                            {
                                Plugin.Log.Info($"Journaled changed caches (hashes: {hashes}, durations: {durations}, song data: {songData}) in {journalStopwatch.Elapsed.TotalSeconds:0.###} seconds.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error("Failed to save song caches:");
                        Plugin.Log.Error(ex);
                    }

                    await RefreshLevelPacksAsync(fullRefresh);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error("Failed to setup LevelPacks:");
                    Plugin.Log.Error(ex);
                }

                #endregion

                AreSongsLoaded = true;
                AreSongsLoading = false;
                LoadingProgress = 1;

                _loadingTask = null;
                await UnityMainThreadTaskScheduler.Factory.StartNew(() => SongsLoadedEvent?.Invoke(this, CustomLevels));
            };

            try
            {
                await (_loadingTask = Task.Run(job, _loadingTaskCancellationTokenSource.Token));
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"Song loading task failed. {ex.Message}");
                return;
            }

            if (_loadingTask.IsCompleted && !_loadingTask.IsCanceled)
            {
                try
                {
                    await Task.Run(finish);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error("Failed to finish song loading:");
                    Plugin.Log.Error(ex);
                }

                await UnityMainThreadTaskScheduler.Factory.StartNew(RunPendingRefresh);
            }
            else
            {
                Plugin.Log.Warn("Song loading task cancelled.");
            }
        }

        internal void StoreLoadedBeatmapSaveData()
        {
            LoadedBeatmapSaveData = new ConcurrentDictionary<string, CustomLevelLoader.LoadedSaveData>(_customLevelLoader._loadedBeatmapSaveData);
        }

        /// <summary>
        /// Delete a beatmap (is only used by other mods)
        /// </summary>
        /// <param name="folderPath">Directory of the beatmap</param>
        /// <param name="deleteFolder">Option to delete the base folder of the beatmap</param>
        public void DeleteSong(string folderPath, bool deleteFolder = true)
        {
            DeleteSingleSong(folderPath, deleteFolder);
            RefreshLevelPacks();
        }

        /// <summary>
        /// Delete multiple beatmaps in bulk (is only used by other mods)
        /// </summary>
        /// <param name="folderPaths">Directories of the beatmaps</param>
        /// <param name="deleteFolder">Option to delete the base folder of the beatmap</param>
        public async Task DeleteSongsAsync(List<string> folderPaths, bool deleteFolder = true)
        {
            foreach (var folderPath in folderPaths)
            {
                await Task.Run(() => DeleteSingleSong(folderPath, deleteFolder));
            }

            RefreshLevelPacks();
        }

        private void DeleteSingleSong(string folderPath, bool deleteFolder)
        {
            if (folderPath.EndsWith("(Built in)", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cannot delete built-in levels.");
            }

            //Remove the level from SongCore Collections
            try
            {
                if (!(CustomLevels.TryRemove(folderPath, out var level) || CustomWIPLevels.TryRemove(folderPath, out level) || CachedWIPLevels.TryRemove(folderPath, out level)))
                {
                    foreach (var folderEntry in SeparateSongFolders)
                    {
                        folderEntry.Levels.TryRemove(folderPath, out level);
                    }
                }

                if (level != null)
                {
                    if (Collections.LevelHashDictionary.TryRemove(level.levelID, out var hash))
                    {
                        Collections.RemoveCustomLevelSongData(level.levelID);
                        if (Collections.HashLevelDictionary.TryGetValue(hash, out var levels))
                        {
                            levels.Remove(level.levelID);
                            if (levels.Count == 0)
                            {
                                Collections.HashLevelDictionary.TryRemove(hash, out _);
                            }
                        }
                    }

                    _customLevelLoader._loadedBeatmapSaveData.Remove(level.levelID);
                    Hashing.MarkSongRemoved(folderPath);
                    _levelsRemovedSinceCacheClear = true;
                }

                //Delete the directory
                if (deleteFolder)
                {
                    if (Directory.Exists(folderPath))
                    {
                        Directory.Delete(folderPath, true);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Exception trying to delete song: {folderPath}");
                Plugin.Log.Error(ex);
            }
        }

        /// <summary>
        /// Load a beatmap, gather all beatmap information and create beatmap preview
        /// </summary>
        /// <param name="loadedSaveData">Loaded save data of beatmap</param>
        /// <param name="hash">Resulting hash for the beatmap, may contain beatmap folder name or 'WIP' at the end</param>
        /// <param name="folderEntry">Folder entry for beatmap folder</param>
        /// <returns></returns>
        private (string hash, BeatmapLevel beatmapLevel)? LoadSong(CustomLevelLoader.LoadedSaveData loadedSaveData, SongFolderEntry? folderEntry = null)
        {
            var wip = loadedSaveData.customLevelFolderInfo.folderPath.Contains("CustomWIPLevels") || folderEntry != null && (folderEntry.Pack == FolderLevelPack.CustomWIPLevels || folderEntry.Pack == FolderLevelPack.CachedWIPLevels || folderEntry.WIP);
            string hash;
            BeatmapLevel? beatmapLevel;
            try
            {
                if (loadedSaveData.standardLevelInfoSaveData != null)
                {
                    hash = Hashing.ComputeCustomLevelHash(loadedSaveData.customLevelFolderInfo, loadedSaveData.standardLevelInfoSaveData);
                    beatmapLevel = _customLevelLoader.CreateBeatmapLevelFromV3(loadedSaveData.customLevelFolderInfo, loadedSaveData.standardLevelInfoSaveData);
                }
                else if (loadedSaveData.beatmapLevelSaveData != null)
                {
                    hash = Hashing.ComputeCustomLevelHash(loadedSaveData.customLevelFolderInfo, loadedSaveData.beatmapLevelSaveData);
                    beatmapLevel = _customLevelLoader.CreateBeatmapLevelFromV4(loadedSaveData.customLevelFolderInfo, loadedSaveData.beatmapLevelSaveData);
                }
                else
                {
                    throw new InvalidOperationException("Level save data is missing.");
                }

                var levelID = CustomLevelLoader.kCustomLevelPrefixId + hash;
                var folderName = new DirectoryInfo(loadedSaveData.customLevelFolderInfo.folderPath).Name;
                while (!Collections.LevelHashDictionary.TryAdd(levelID + (wip ? " WIP" : ""), hash))
                {
                    levelID += $"_{folderName}";
                }

                if (wip)
                {
                    levelID += " WIP";
                }

                Collections.HashLevelDictionary.AddOrUpdate(hash, new List<string> { levelID }, (_, levels) =>
                {
                    lock (levels)
                    {
                        levels.Add(levelID);
                    }
                    return levels;
                });
                // Computed at most once per beatmap per refresh, and shared by all three caches so that
                // validating them does not cost extra file access.
                var directoryHash = Hashing.GetCurrentDirectoryHash(loadedSaveData.customLevelFolderInfo.folderPath);
                Collections.CreateCustomLevelSongData(levelID, directoryHash, loadedSaveData);
                LoadedBeatmapSaveData.TryAdd(levelID, loadedSaveData);

                Accessors.LevelIDAccessor(ref beatmapLevel) = levelID;
                GetSongDuration(loadedSaveData, beatmapLevel, directoryHash);
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"Failed to load song: {loadedSaveData.customLevelFolderInfo.folderPath}");
                Plugin.Log.Error(e);
                return null;
            }

            return (hash, beatmapLevel);
        }

        #region HelperFunctionsZIP

        /// <summary>
        /// Extracts beatmap ZIP files to the cache folder
        /// </summary>
        /// <param name="cachePath">Directory of cache folder</param>
        /// <param name="songFolderPath">Directory of folder containing the zips</param>
        private static void CacheZIPs(string cachePath, string songFolderPath)
        {
            if (!Directory.Exists(cachePath))
            {
                Directory.CreateDirectory(cachePath);
            }

            var cache = new DirectoryInfo(cachePath);
            foreach (var file in cache.EnumerateFiles())
            {
                file.Delete();
            }

            foreach (var folder in cache.EnumerateDirectories())
            {
                folder.Delete(true);
            }

            var zips = Directory.EnumerateFiles(songFolderPath, "*.zip");

            foreach (var zip in zips)
            {
                try
                {
                    using var fileStream = File.OpenRead(zip);
                    using var archive = new ZipArchive(fileStream);
                    archive.ExtractToDirectory(Path.Combine(cachePath, Path.GetFileName(zip)));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warn($"Failed to extract zip: {zip}:");
                    Plugin.Log.Warn(ex);
                }
            }
        }

        /// <summary>
        /// Loads the beatmaps of the cached
        /// </summary>
        /// <param name="cacheFolders">Directory of cache folder</param>
        /// <param name="fullRefresh"></param>
        /// <param name="beatmapDictionary"></param>
        /// <param name="folderEntry"></param>
        private Task LoadCachedZIPs(IEnumerable<string> cacheFolders, bool fullRefresh, ConcurrentDictionary<string, BeatmapLevel> beatmapDictionary, SongFolderEntry? folderEntry = null)
        {
            var tasks = new List<Task>();
            foreach (var cachedFolder in cacheFolders)
            {
                if (_loadingTaskCancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                string[] results;
                try
                {
                    results = Directory.GetFiles(cachedFolder, CustomLevelPathHelper.kStandardLevelInfoFilename);
                }
                catch (DirectoryNotFoundException)
                {
                    Plugin.Log.Warn($"Skipping missing or corrupt folder: '{cachedFolder}'");
                    continue;
                }

                if (results.Length == 0)
                {
                    Plugin.Log.Warn($"Folder: '{cachedFolder}' is missing {CustomLevelPathHelper.kStandardLevelInfoFilename} files!");
                    continue;
                }

                foreach (var result in results)
                {
                    try
                    {
                        var songPath = Path.GetDirectoryName(result)!;
                        if (!fullRefresh)
                        {
                            if (SearchBeatmapInMapPack(beatmapDictionary, songPath))
                            {
                                continue;
                            }
                        }

                        tasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                var customLevel = LoadCustomLevel(songPath, folderEntry);
                                if (!customLevel.HasValue)
                                {
                                    Plugin.Log.Error($"Failed to load custom level: {songPath}");
                                    return;
                                }

                                var (_, level) = customLevel.Value;
                                beatmapDictionary[songPath] = level;
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.Error($"Failed to load song from {cachedFolder}:");
                                Plugin.Log.Error(ex);
                            }
                        }, _loadingTaskCancellationTokenSource.Token));
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Error($"Failed to load song from {cachedFolder}:");
                        Plugin.Log.Error(ex);
                    }
                }
            }

            return Task.WhenAll(tasks);
        }

        #endregion

        #region HelperFunctionsLoading

        // Taken once on the first refresh and kept for the session. Changing the affinity while the game is
        // running is therefore not picked up until the next launch.
        private static readonly Lazy<int> LoadingParallelism = new Lazy<int>(() =>
        {
            var (processorCount, fromAffinity) = GetAvailableProcessorCount();
            var parallelism = Math.Max(1, processorCount / 2 - 1);
            Plugin.Log.Info($"Available processors: {processorCount} ({(fromAffinity ? "process affinity" : "Environment.ProcessorCount")}), song loading parallelism: {parallelism}");
            return parallelism;
        });

        /// <summary>
        /// Number of logical processors the game is allowed to run on. Environment.ProcessorCount does not take
        /// the process affinity into account on Mono, so the affinity mask is counted when it can be read.
        /// </summary>
        private static (int count, bool fromAffinity) GetAvailableProcessorCount()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var mask = unchecked((ulong)process.ProcessorAffinity.ToInt64());
                var count = 0;
                while (mask != 0)
                {
                    mask &= mask - 1;
                    count++;
                }

                if (count > 0)
                {
                    return (count, true);
                }
            }
            catch (Exception)
            {
                // Not supported on every platform. Falls back to the processor count below.
            }

            return (Environment.ProcessorCount, false);
        }

        private bool IsLoadedInSeparateFolderPack(SeparateSongFolder entry, string songPath)
        {
            return entry.SongFolderEntry.Pack switch
            {
                FolderLevelPack.NewPack => SearchBeatmapInMapPack(entry.Levels, songPath),
                FolderLevelPack.CustomLevels => SearchBeatmapInMapPack(CustomLevels, songPath),
                FolderLevelPack.CustomWIPLevels => SearchBeatmapInMapPack(CustomWIPLevels, songPath),
                FolderLevelPack.CachedWIPLevels => SearchBeatmapInMapPack(CachedWIPLevels, songPath),
                _ => false
            };
        }

        private bool SearchBeatmapInMapPack(ConcurrentDictionary<string, BeatmapLevel> mapPack, string songPath)
        {
            if (mapPack.TryGetValue(songPath, out var beatmapLevel) && beatmapLevel != null)
            {
                return true;
            }

            return false;
        }

        private bool AssignBeatmapToSeparateFolder(
            ConcurrentDictionary<string, BeatmapLevel> mapPack,
            string songPath,
            ConcurrentDictionary<string, BeatmapLevel> separateFolder)
        {
            if (mapPack.TryGetValue(songPath, out var beatmapLevel) && beatmapLevel != null)
            {
                separateFolder[songPath] = beatmapLevel;
                return true;
            }

            return false;
        }

        public static (string hash, BeatmapLevel beatmapLevel)? LoadCustomLevel(string customLevelPath, SongFolderEntry? entry = null)
        {
            return Instance.LoadCustomLevelInternal(customLevelPath, entry);
        }

        private (string hash, BeatmapLevel beatmapLevel)? LoadCustomLevelInternal(string customLevelPath, SongFolderEntry? entry = null)
        {
            var infoFilePath = Path.Combine(customLevelPath, CustomLevelPathHelper.kStandardLevelInfoFilename);
            if (!File.Exists(infoFilePath))
            {
                return null;
            }

            CustomLevelLoader.LoadedSaveData loadedSaveData;
            var directoryInfo = new DirectoryInfo(customLevelPath);
            var json = File.ReadAllText(infoFilePath);
            var version = BeatmapSaveDataHelpers.GetVersion(json);
            if (version < BeatmapSaveDataHelpers.version4)
            {
                var standardLevelInfoSaveData = StandardLevelInfoSaveData.DeserializeFromJSONString(json);
                if (standardLevelInfoSaveData == null || standardLevelInfoSaveData.difficultyBeatmapSets.Length == 0)
                {
                    return null;
                }

                var customLevelFolderInfo = new CustomLevelFolderInfo(directoryInfo.FullName, directoryInfo.Name, json);
                if (!CustomLevelLoader.CheckIfAudioExists(customLevelFolderInfo, standardLevelInfoSaveData.songFilename))
                {
                    return null;
                }

                loadedSaveData = new CustomLevelLoader.LoadedSaveData { customLevelFolderInfo = customLevelFolderInfo, standardLevelInfoSaveData = standardLevelInfoSaveData };
            }
            else
            {
                var beatmapLevelSaveData = JsonConvert.DeserializeObject<BeatmapLevelSaveData>(json, JsonSettings.readableWithDefault);
                if (beatmapLevelSaveData == null || beatmapLevelSaveData.difficultyBeatmaps.Length == 0)
                {
                    return null;
                }

                var customLevelFolderInfo = new CustomLevelFolderInfo(directoryInfo.FullName, directoryInfo.Name, json);
                if (!CustomLevelLoader.CheckIfAudioExists(customLevelFolderInfo, beatmapLevelSaveData.audio.songFilename))
                {
                    return null;
                }

                BeatmapLevelSaveDataUtils.MigrateBeatmapLevelSaveData(beatmapLevelSaveData);
                loadedSaveData = new CustomLevelLoader.LoadedSaveData { customLevelFolderInfo = customLevelFolderInfo, beatmapLevelSaveData = beatmapLevelSaveData };
            }

            return LoadSong(loadedSaveData, entry);
        }

        #endregion

        #region HelperFunctionsSearching

        /// <summary>
        /// Attempts to get a beatmap by LevelId. Returns null a matching level isn't found.
        /// </summary>
        /// <param name="levelId"></param>
        /// <returns></returns>
        public static BeatmapLevel? GetLevelById(string levelId)
        {
            if (string.IsNullOrEmpty(levelId))
            {
                return null;
            }

            return BeatmapLevelsModelSO.GetBeatmapLevel(levelId);
        }

        /// <summary>
        /// Attempts to get a custom level by hash (case-insensitive). Returns null a matching custom level isn't found.
        /// </summary>
        /// <param name="hash"></param>
        /// <returns></returns>
        public static BeatmapLevel? GetLevelByHash(string hash)
        {
            if (string.IsNullOrEmpty(hash))
            {
                return null;
            }

            return BeatmapLevelsModelSO.GetBeatmapLevel(CustomLevelLoader.kCustomLevelPrefixId + hash, true);
        }

        private void GetSongDuration(CustomLevelLoader.LoadedSaveData loadedSaveData, BeatmapLevel beatmapLevel, long directoryHash)
        {
            try
            {
                float length = 0;
                Hashing.TryGetRelativePath(loadedSaveData.customLevelFolderInfo.folderPath, out var relativePath);
                if (Hashing.cachedAudioData.TryGetValue(relativePath, out var data))
                {
                    // Validated against the beatmap folder rather than the level ID, which is unstable for
                    // beatmaps that share a hash and would cause the audio file to be parsed every refresh.
                    if (data.directoryHash == directoryHash)
                    {
                        length = data.duration;
                    }
                }

                if (length == 0)
                {
                    try
                    {
                        if (loadedSaveData.standardLevelInfoSaveData != null)
                        {
                            length = GetLengthFromOgg(Path.Combine(loadedSaveData.customLevelFolderInfo.folderPath, loadedSaveData.standardLevelInfoSaveData.songFilename));
                        }
                        else if (loadedSaveData.beatmapLevelSaveData != null)
                        {
                            length = GetLengthFromOgg(Path.Combine(loadedSaveData.customLevelFolderInfo.folderPath, loadedSaveData.beatmapLevelSaveData.audio.songFilename));
                        }
                    }
                    catch (Exception)
                    {
                        length = -1;
                    }

                    if (length <= 1)
                    {
                        // janky, but whatever
                        Plugin.Log.Warn($"Failed to parse song length from audio file, approximating using map length. Song: {loadedSaveData.customLevelFolderInfo.folderPath}");

                        IBeatmapLevelData beatmapLevelData = null!;

                        if (loadedSaveData.standardLevelInfoSaveData != null)
                        {
                            beatmapLevelData = _customLevelLoader.CreateBeatmapLevelDataFromV3(loadedSaveData.customLevelFolderInfo, loadedSaveData.standardLevelInfoSaveData);
                        }
                        else if (loadedSaveData.beatmapLevelSaveData != null)
                        {
                            beatmapLevelData = _customLevelLoader.CreateBeatmapLevelDataFromV4(loadedSaveData.customLevelFolderInfo, loadedSaveData.beatmapLevelSaveData);
                        }

                        length = GetLengthFromMap(loadedSaveData, beatmapLevelData, beatmapLevel);
                    }

                    // Stored through the cache so that the change is journaled and the snapshot is known to be
                    // outdated. Writing it into the cached entry directly would get lost on the next launch.
                    // Nothing is stored when the value did not change, which happens for beatmaps whose
                    // duration cannot be determined, so that they do not force a snapshot on every launch.
                    if (data == null || data.directoryHash != directoryHash || data.duration != length)
                    {
                        Hashing.StoreAudioDuration(relativePath, directoryHash, length);
                    }
                }

                if (_config.ForceLongPreviews)
                {
                    Accessors.PreviewDurationAccessor(ref beatmapLevel) = Mathf.Max(beatmapLevel.previewDuration, length - beatmapLevel.previewStartTime);
                }

                Accessors.SongDurationAccessor(ref beatmapLevel) = length;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Failed to parse song duration");
                Plugin.Log.Warn(ex);
            }
        }

        public static float GetLengthFromMap(CustomLevelLoader.LoadedSaveData loadedSaveData, IBeatmapLevelData beatmapLevelData, BeatmapLevel level)
        {
            float length = 0;

            if (loadedSaveData.standardLevelInfoSaveData != null)
            {
                var beatmapSaveData = JsonUtility.FromJson<BeatmapSaveDataVersion3.BeatmapSaveData>(beatmapLevelData.GetBeatmapString(level.GetBeatmapKeys().Last()));

                float highestTime = 0;
                if (beatmapSaveData.colorNotes.Count > 0)
                {
                    highestTime = beatmapSaveData.colorNotes.Max(x => x.beat);
                }
                else if (beatmapSaveData.basicBeatmapEvents.Count > 0)
                {
                    highestTime = beatmapSaveData.basicBeatmapEvents.Max(x => x.beat);
                }

                var bpmTimeProcessor = new BpmTimeProcessor(level.beatsPerMinute, beatmapSaveData.bpmEvents);
                length = bpmTimeProcessor.ConvertBeatToTime(highestTime);
            }
            else if (loadedSaveData.beatmapLevelSaveData != null)
            {
                var beatmapKey = level.GetBeatmapKeys().Last();
                var beatmapSaveData = JsonUtility.FromJson<BeatmapSaveData>(beatmapLevelData.GetBeatmapString(beatmapKey));
                var lightshowSaveData = JsonUtility.FromJson<LightshowSaveData>(beatmapLevelData.GetLightshowString(beatmapKey));
                var audioSaveData = JsonUtility.FromJson<AudioSaveData>(beatmapLevelData.GetAudioDataString());

                float highestTime = 0;
                if (beatmapSaveData.colorNotes.Length > 0)
                {
                    highestTime = beatmapSaveData.colorNotes.Max(x => x.beat);
                }
                else if (lightshowSaveData.basicEvents.Length > 0)
                {
                    highestTime = lightshowSaveData.basicEvents.Max(x => x.beat);
                }

                var bpmTimeProcessor = new BpmTimeProcessor(audioSaveData);
                length = bpmTimeProcessor.ConvertBeatToTime(highestTime);
            }

            if (length == 0)
            {
                Plugin.Log.Warn($"Failed to parse song length from audio file. Song: {loadedSaveData.customLevelFolderInfo.folderPath}");
            }

            return length;
        }

        private static readonly byte[] oggBytes =
        {
            0x4F,
            0x67,
            0x67,
            0x53,
            0x00,
            0x04
        };

        public static float GetLengthFromOgg(string oggFile)
        {
            using var fs = File.OpenRead(oggFile);
            using var br = new BinaryReader(fs, Encoding.ASCII);

            /*
                 * Tries to find the array of bytes from the stream
                 */
            bool FindBytes(byte[] bytes, int searchLength)
            {
                for (var i = 0; i < searchLength; i++)
                {
                    var b = br.ReadByte();
                    if (b != bytes[0])
                    {
                        continue;
                    }

                    var by = br.ReadBytes(bytes.Length - 1);
                    // hardcoded 6 bytes compare, is fine because all inputs used are 6 bytes
                    // bitwise AND the last byte to read only the flag bit for lastSample searching
                    // shouldn't cause issues finding rate, hopefully
                    if (by[0] == bytes[1] && by[1] == bytes[2] && by[2] == bytes[3] && by[3] == bytes[4] && (by[4] & bytes[5]) == bytes[5])
                    {
                        return true;
                    }

                    var index = Array.IndexOf(by, bytes[0]);
                    if (index != -1)
                    {
                        fs.Position += index - (bytes.Length - 1);
                        i += index;
                    }
                    else
                    {
                        i += (bytes.Length - 1);
                    }
                }

                return false;
            }

            var rate = -1;
            long lastSample = -1;

            //Skip Capture Pattern
            fs.Position = 24;

            //{0x76, 0x6F, 0x72, 0x62, 0x69, 0x73} = "vorbis" in byte values
            var foundVorbis = FindBytes(new byte[]
            {
                0x76,
                0x6F,
                0x72,
                0x62,
                0x69,
                0x73
            }, 256);
            if (foundVorbis)
            {
                fs.Position += 5;
                rate = br.ReadInt32();
            }
            else
            {
                Plugin.Log.Warn($"Could not find rate for {oggFile}");
                return -1;
            }

            /*
                 * this finds the last occurrence of OggS in the file by checking for a bit flag (0x04)
                 * reads in blocks determined by seekBlockSize
                 * 6144 does not add significant overhead and speeds up the search significantly
                 */
            const int seekBlockSize = 6144;
            const int seekTries = 10; // 60 KiB should be enough for any sane ogg file
            for (var i = 0; i < seekTries; i++)
            {
                var seekPos = (i + 1) * seekBlockSize * -1;
                var overshoot = Math.Max((int) (-seekPos - fs.Length), 0);
                if (overshoot >= seekBlockSize)
                {
                    break;
                }

                fs.Seek(seekPos + overshoot, SeekOrigin.End);
                var foundOggS = FindBytes(oggBytes, seekBlockSize - overshoot);
                if (foundOggS)
                {
                    lastSample = br.ReadInt64();
                    break;
                }
            }

            if (lastSample == -1)
            {
                Plugin.Log.Warn($"Could not find lastSample for {oggFile}");
                return -1;
            }

            var length = lastSample / (float) rate;
            return length;
        }

        #endregion
    }
}

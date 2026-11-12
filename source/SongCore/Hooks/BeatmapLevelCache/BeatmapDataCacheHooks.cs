using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MonoMod.RuntimeDetour;
using Zenject;

namespace SongCore.Hooks.BeatmapLevelCache
{
    /// <summary>
    /// Keeps <see cref="BeatmapLevelCache"/> describing what the game has selected and loaded, for mods built against
    /// 3.16.0. Everything is only observed: the game's methods always run and their results are always the game's own.
    /// <list type="bullet">
    ///   <item>Selecting a level starts a new selection. For custom levels the level data is taken right away from the
    ///   game's own loader, so it is there before any handler of the selection event runs.</item>
    ///   <item>For official levels, the level data the game loads for the selection is recorded once it is done.</item>
    ///   <item>The beatmap key the game creates is recorded once the level data is there.</item>
    ///   <item>The beatmap data the game loads for the selected level is recorded with its request.</item>
    /// </list>
    /// Unlike 3.16.0, the handlers of other mods are not delayed until all of this is done. Mods can wait for it with
    /// <see cref="BeatmapLevelCache.WaitUntilReadyAsync"/>, as before.
    /// </summary>
    internal class BeatmapDataCacheHooks : IInitializable, IDisposable
    {
        private readonly CustomLevelLoader _customLevelLoader;
        private readonly BeatmapLevelsModel _beatmapLevelsModel;
        private readonly BeatmapLevelsEntitlementModel _beatmapLevelsEntitlementModel;
        private readonly BeatmapDataLoader _beatmapDataLoader;
        private readonly BeatmapLevelCache _beatmapLevelCache;
        private readonly EventProxyHooks _eventProxyHooks;

        private Hook _selectLevelHook = null!;
        private Hook _createBeatmapKeyHook = null!;
        private Hook _loadBeatmapLevelDataAsyncHook = null!;
        private Hook _showContentHook = null!;
        private Hook _loadBeatmapDataAsyncHook = null!;
        private CancellationToken _cancellationToken;

        // Completed when the game has loaded the level data of the selected official level.
        private TaskCompletionSource<IBeatmapLevelData?>? _officialLevelDataSource;

        private BeatmapDataCacheHooks(CustomLevelLoader customLevelLoader, BeatmapLevelsModel beatmapLevelsModel, BeatmapLevelsEntitlementModel beatmapLevelsEntitlementModel, BeatmapDataLoader beatmapDataLoader, BeatmapLevelCache beatmapLevelCache, EventProxyHooks eventProxyHooks)
        {
            _customLevelLoader = customLevelLoader;
            _beatmapLevelsModel = beatmapLevelsModel;
            _beatmapLevelsEntitlementModel = beatmapLevelsEntitlementModel;
            _beatmapDataLoader = beatmapDataLoader;
            _beatmapLevelCache = beatmapLevelCache;
            _eventProxyHooks = eventProxyHooks;
        }

        public void Initialize()
        {
            _selectLevelHook = new Hook(typeof(LevelCollectionTableView).GetMethod(nameof(LevelCollectionTableView.HandleDidSelectCellWithIndex), BindingFlags.Instance | BindingFlags.NonPublic)!, HandleDidSelectCellWithIndex, true);
            _createBeatmapKeyHook = new Hook(typeof(StandardLevelDetailView).GetMethod(nameof(StandardLevelDetailView.CreateBeatmapKey), BindingFlags.Instance | BindingFlags.NonPublic)!, RecordBeatmapKey, true);
            _loadBeatmapLevelDataAsyncHook = new Hook(typeof(BeatmapLevelsModel).GetMethod(nameof(BeatmapLevelsModel.LoadBeatmapLevelDataAsync))!, RecordBeatmapLevelData, true);
            _showContentHook = new Hook(typeof(StandardLevelDetailViewController).GetMethod(nameof(StandardLevelDetailViewController.ShowContent), BindingFlags.Instance | BindingFlags.NonPublic)!, ReleaseUnloadableLevelData, true);
            _loadBeatmapDataAsyncHook = new Hook(typeof(BeatmapDataLoader).GetMethod(nameof(BeatmapDataLoader.LoadBeatmapDataAsync))!, RecordBeatmapData, true);
        }

        public void Dispose()
        {
            _selectLevelHook.Dispose();
            _createBeatmapKeyHook.Dispose();
            _loadBeatmapLevelDataAsyncHook.Dispose();
            _showContentHook.Dispose();
            _loadBeatmapDataAsyncHook.Dispose();

            _officialLevelDataSource?.TrySetResult(null);
            _beatmapLevelCache.CancellationTokenSource?.Cancel();
        }

        private void HandleDidSelectCellWithIndex(Action<LevelCollectionTableView, HMUI.TableView, int> original, LevelCollectionTableView instance, HMUI.TableView tableView, int row)
        {
            try
            {
                // Resolved the same way the game does right after this, before it raises the selection event.
                var index = row;
                if (instance._showLevelPackHeader)
                {
                    index--;
                }

                var beatmapLevels = instance._beatmapLevels;
                if (index >= 0 && beatmapLevels != null && index < beatmapLevels.Count)
                {
                    var beatmapLevel = beatmapLevels[index];
                    Plugin.Log.Debug($"Selected level: {beatmapLevel.levelID} | {beatmapLevel.songName}");

                    _beatmapLevelCache.Init(beatmapLevel, GetBeatmapLevelDataAsync);
                    _cancellationToken = _beatmapLevelCache.CancellationTokenSource!.Token;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"An error occurred while recording the selected level.\n{ex}");
            }

            original(instance, tableView, row);
        }

        private Task<IBeatmapLevelData?> GetBeatmapLevelDataAsync(BeatmapLevel beatmapLevel, CancellationToken cancellationToken)
        {
            _officialLevelDataSource?.TrySetResult(null);
            _officialLevelDataSource = null;

            if (!beatmapLevel.hasPrecalculatedData)
            {
                // Creating the level data of a custom level only arranges what was read when the level was loaded,
                // and the game keeps the result, so this is the same object the game uses afterwards.
                var beatmapLevelData = _customLevelLoader.LoadBeatmapLevelData(beatmapLevel);
                _beatmapLevelCache.BeatmapLevelData = beatmapLevelData;
                return Task.FromResult<IBeatmapLevelData?>(beatmapLevelData);
            }

            // Official levels are loaded from asset bundles, which can only be done asynchronously and may need a
            // download, so this waits for the game to load it.
            var source = new TaskCompletionSource<IBeatmapLevelData?>();
            cancellationToken.Register(() => source.TrySetResult(null));
            _officialLevelDataSource = source;
            return source.Task;
        }

        private Task<LoadBeatmapLevelDataResult> RecordBeatmapLevelData(Func<BeatmapLevelsModel, string, BeatmapLevelDataVersion, CancellationToken, Task<LoadBeatmapLevelDataResult>> original, BeatmapLevelsModel instance, string levelID, BeatmapLevelDataVersion beatmapLevelDataVersion, CancellationToken cancellationToken)
        {
            var task = original(instance, levelID, beatmapLevelDataVersion, cancellationToken);

            var source = _officialLevelDataSource;
            if (source != null && _beatmapLevelCache.BeatmapLevel?.levelID == levelID)
            {
                var selectionToken = _cancellationToken;
                task.ContinueWith(completed =>
                {
                    if (selectionToken.IsCancellationRequested)
                    {
                        return;
                    }

                    if (completed.Status != TaskStatus.RanToCompletion || completed.Result.isError)
                    {
                        // The game did not get the level data either, so there is nothing to wait for.
                        source.TrySetResult(null);
                        return;
                    }

                    _beatmapLevelCache.BeatmapLevelData = completed.Result.beatmapLevelData;
                    source.TrySetResult(completed.Result.beatmapLevelData);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            return task;
        }

        private void ReleaseUnloadableLevelData(Action<StandardLevelDetailViewController, StandardLevelDetailViewController.ContentType, string> original, StandardLevelDetailViewController instance, StandardLevelDetailViewController.ContentType contentType, string errorText)
        {
            original(instance, contentType, errorText);

            // The game does not load the level data of levels it cannot play here, so nothing waiting for it is left
            // waiting until the next selection.
            if (contentType is StandardLevelDetailViewController.ContentType.Buy or StandardLevelDetailViewController.ContentType.BuyInfo
                or StandardLevelDetailViewController.ContentType.Error or StandardLevelDetailViewController.ContentType.NoAllowedDifficultyBeatmap)
            {
                _officialLevelDataSource?.TrySetResult(null);
            }
        }

        private BeatmapKey RecordBeatmapKey(Func<StandardLevelDetailView, BeatmapKey> original, StandardLevelDetailView instance)
        {
            var result = original(instance);
            RecordBeatmapKeyAsync(result);
            return result;
        }

        private async void RecordBeatmapKeyAsync(BeatmapKey beatmapKey)
        {
            try
            {
                var tcs = _beatmapLevelCache.BeatmapKeyTaskCompletionSource;
                var loadingTask = _beatmapLevelCache.BeatmapLevelDataLoadingTask;
                if (tcs == null || loadingTask == null)
                {
                    return;
                }

                // Completes right away for custom levels, so the key is recorded before the game reads the beatmap.
                var beatmapLevelData = await loadingTask;

                // Another level has been selected in the meantime.
                if (tcs != _beatmapLevelCache.BeatmapKeyTaskCompletionSource)
                {
                    return;
                }

                if (beatmapLevelData == null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                if (!_beatmapLevelCache.DifficultyMatches(beatmapKey))
                {
                    Plugin.Log.Debug($"Setting beatmap level cache beatmap key to {beatmapKey}");

                    _beatmapLevelCache.InvalidateDifficulty();
                    _beatmapLevelCache.BeatmapKey = beatmapKey;
                }

                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"An error occurred while recording the beatmap key.\n{ex}");
            }
        }

        private Task<IReadonlyBeatmapData?> RecordBeatmapData(Func<BeatmapDataLoader, IBeatmapLevelData, BeatmapKey, float, bool, IEnvironmentInfo?, IEnvironmentInfo?, BeatmapLevelDataVersion, GameplayModifiers?, PlayerSpecificSettings?, bool, Task<IReadonlyBeatmapData?>> original, BeatmapDataLoader instance, IBeatmapLevelData beatmapLevelData, BeatmapKey beatmapKey, float startBpm, bool loadingForDesignatedEnvironment, IEnvironmentInfo? targetEnvironmentInfo, IEnvironmentInfo? originalEnvironmentInfo, BeatmapLevelDataVersion beatmapLevelDataVersion, GameplayModifiers? gameplayModifiers, PlayerSpecificSettings? playerSpecificSettings, bool enableBeatmapDataCaching)
        {
            var task = original(instance, beatmapLevelData, beatmapKey, startBpm, loadingForDesignatedEnvironment, targetEnvironmentInfo, originalEnvironmentInfo, beatmapLevelDataVersion, gameplayModifiers, playerSpecificSettings, enableBeatmapDataCaching);

            try
            {
                if (_beatmapLevelCache.LevelMatches(beatmapLevelData))
                {
                    _beatmapLevelCache.BeatmapDataRequest = new BeatmapDataRequest(beatmapLevelData, beatmapKey, startBpm, loadingForDesignatedEnvironment, targetEnvironmentInfo, originalEnvironmentInfo, beatmapLevelDataVersion, gameplayModifiers, playerSpecificSettings, enableBeatmapDataCaching);
                    _beatmapLevelCache.BeatmapDataLoadingTask = task;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"An error occurred while recording the beatmap data.\n{ex}");
            }

            return task;
        }
    }
}

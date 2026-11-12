using System;
using System.Threading;
using System.Threading.Tasks;

namespace SongCore.Hooks.BeatmapLevelCache
{
    /// <summary>
    /// The beatmap level cache of 3.16.0, kept for mods that read it through reflection. It no longer caches anything:
    /// it only describes what the game currently has selected and has loaded for it, as recorded by
    /// <see cref="BeatmapDataCacheHooks"/> and <see cref="BeatmapJsonCacheHooks"/>. Nothing in here is ever handed back
    /// to the game in place of loading, which is what made the original return data of another difficulty.
    /// </summary>
    internal class BeatmapLevelCache
    {
        private static readonly int BeatmapDataTypeCount = Enum.GetValues(typeof(BeatmapDataType)).Length;

        // The JSON the game last read for the selected level and difficulty, indexed by BeatmapDataType, and the reads
        // still in progress. Cleared whenever the level or difficulty changes, so they never belong to another one.
        private readonly string?[] _jsonData = new string?[BeatmapDataTypeCount];
        private readonly Task<string?>?[] _jsonTasks = new Task<string?>?[BeatmapDataTypeCount];

        // Reads of the JSON can finish on other threads.
        private readonly object _jsonLock = new object();

        public CancellationTokenSource? CancellationTokenSource { get; private set; }
        public BeatmapLevel? BeatmapLevel { get; private set; }
        public TaskCompletionSource<bool>? BeatmapKeyTaskCompletionSource { get; private set; }
        public Task<IBeatmapLevelData?>? BeatmapLevelDataLoadingTask { get; private set; }
        public IBeatmapLevelData? BeatmapLevelData { get; set; }
        public BeatmapKey BeatmapKey { get; set; }
        public BeatmapDataRequest? BeatmapDataRequest { get; set; }
        public Task<IReadonlyBeatmapData?>? BeatmapDataLoadingTask { get; set; }

        public void Init(BeatmapLevel beatmapLevel, Func<BeatmapLevel, CancellationToken, Task<IBeatmapLevelData?>> loadBeatmapLevelDataFunc)
        {
            CancellationTokenSource?.Cancel();

            // Anything still waiting for the previous level is released rather than left waiting forever.
            BeatmapKeyTaskCompletionSource?.TrySetResult(false);

            InvalidateLevel();
            CancellationTokenSource = new CancellationTokenSource();
            BeatmapKeyTaskCompletionSource = new TaskCompletionSource<bool>();
            BeatmapLevel = beatmapLevel;
            BeatmapLevelDataLoadingTask = loadBeatmapLevelDataFunc(beatmapLevel, CancellationTokenSource.Token);
        }

        public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            Plugin.Log.Debug("Waiting for beatmap level cache to be ready");

            if (cancellationToken.IsCancellationRequested || BeatmapLevelDataLoadingTask == null || BeatmapKeyTaskCompletionSource == null)
            {
                await Task.CompletedTask;
                return;
            }

            await BeatmapLevelDataLoadingTask;

            if (cancellationToken.IsCancellationRequested)
            {
                await Task.CompletedTask;
                return;
            }

            await BeatmapKeyTaskCompletionSource.Task;
        }

        public bool LevelMatches(IBeatmapLevelData beatmapLevelData)
        {
            return beatmapLevelData == BeatmapLevelData;
        }

        public bool DifficultyMatches(BeatmapKey beatmapKey)
        {
            return beatmapKey.IsValid() && beatmapKey == BeatmapKey;
        }

        public void InvalidateLevel()
        {
            BeatmapLevelData = null;
            lock (_jsonLock)
            {
                _jsonData[(int)BeatmapDataType.Audio] = null;
                _jsonTasks[(int)BeatmapDataType.Audio] = null;
            }

            InvalidateDifficulty();
        }

        public void InvalidateDifficulty()
        {
            BeatmapKey = new BeatmapKey();
            BeatmapDataRequest = null;
            BeatmapDataLoadingTask = null;

            lock (_jsonLock)
            {
                Array.Clear(_jsonData, 1, _jsonData.Length - 1);
                Array.Clear(_jsonTasks, 1, _jsonTasks.Length - 1);
            }
        }

        /// <summary>
        /// Always reads through <paramref name="original"/>, and records the result if it belongs to the current
        /// selection.
        /// </summary>
        public string? GetJsonData(IBeatmapLevelData beatmapLevelData, BeatmapKey beatmapKey, BeatmapDataType beatmapDataType, Func<string?> original)
        {
            var allowIO = IOBlacklistHooks.AllowIO.Value;
            IOBlacklistHooks.AllowIO.Value = true;
            try
            {
                var data = original();
                RecordJsonData(beatmapLevelData, beatmapKey, beatmapDataType, data, null);
                return data;
            }
            finally
            {
                IOBlacklistHooks.AllowIO.Value = allowIO;
            }
        }

        /// <summary>
        /// Always reads through <paramref name="original"/>, and records the read while it is in progress and its result
        /// once it is done, if it belongs to the current selection. The task of the original read is returned as-is.
        /// </summary>
        public Task<string?> GetJsonDataAsync(IBeatmapLevelData beatmapLevelData, BeatmapKey beatmapKey, BeatmapDataType beatmapDataType, Func<Task<string?>> original)
        {
            Task<string?> task;
            var allowIO = IOBlacklistHooks.AllowIO.Value;
            IOBlacklistHooks.AllowIO.Value = true;
            try
            {
                task = original();
            }
            finally
            {
                IOBlacklistHooks.AllowIO.Value = allowIO;
            }

            var idx = (int)beatmapDataType;
            lock (_jsonLock)
            {
                if (Matches(beatmapLevelData, beatmapKey, beatmapDataType))
                {
                    _jsonTasks[idx] = task;
                }
            }

            task.ContinueWith(completed =>
            {
                RecordJsonData(beatmapLevelData, beatmapKey, beatmapDataType, completed.Status == TaskStatus.RanToCompletion ? completed.Result : null, completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return task;
        }

        private void RecordJsonData(IBeatmapLevelData beatmapLevelData, BeatmapKey beatmapKey, BeatmapDataType beatmapDataType, string? data, Task<string?>? task)
        {
            var idx = (int)beatmapDataType;
            lock (_jsonLock)
            {
                if (task != null && _jsonTasks[idx] == task)
                {
                    _jsonTasks[idx] = null;
                }

                // A read that failed leaves whatever was recorded before.
                if (data != null && Matches(beatmapLevelData, beatmapKey, beatmapDataType))
                {
                    _jsonData[idx] = data;
                }
            }
        }

        private bool Matches(IBeatmapLevelData beatmapLevelData, BeatmapKey beatmapKey, BeatmapDataType beatmapDataType)
        {
            return LevelMatches(beatmapLevelData) && (beatmapDataType == BeatmapDataType.Audio || DifficultyMatches(beatmapKey));
        }
    }
}

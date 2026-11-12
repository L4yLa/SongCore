using System;
using System.Threading.Tasks;
using MonoMod.RuntimeDetour;
using Zenject;

namespace SongCore.Hooks.BeatmapLevelCache
{
    /// <summary>
    /// Records the JSON the game reads for the selected level and difficulty in <see cref="BeatmapLevelCache"/>, for mods
    /// built against 3.16.0. The game's own read always runs and its result is passed on as-is. Unlike 3.16.0, nothing is
    /// handed back from the cache, which could return the JSON of another difficulty.
    /// </summary>
    internal class BeatmapJsonCacheHooks : IInitializable, IDisposable
    {
        private readonly BeatmapLevelCache _beatmapLevelCache;

        private Hook _getBeatmapStringAsyncHook = null!;
        private Hook _getLightshowStringAsyncHook = null!;
        private Hook _getAudioStringAsyncHook = null!;
        private Hook _getAudioStringHook = null!;
        private Hook _getBeatmapStringHook = null!;
        private Hook _getLightshowStringHook = null!;
        private Hook _getOstBeatmapStringAsyncHook = null!;
        private Hook _getOstLightshowStringAsyncHook = null!;
        private Hook _getOstAudioStringAsyncHook = null!;
        private Hook _getOstAudioStringHook = null!;
        private Hook _getOstBeatmapStringHook = null!;
        private Hook _getOstLightshowStringHook = null!;

        private delegate Task<string?> GetStringAsyncDelegate(FileSystemBeatmapLevelData self, in BeatmapKey beatmapKey);
        private delegate string? GetStringDelegate(FileSystemBeatmapLevelData self, in BeatmapKey beatmapKey);
        private delegate Task<string?> GetOstStringAsyncDelegate(BeatmapLevelDataSO self, in BeatmapKey beatmapKey);
        private delegate string? GetOstStringDelegate(BeatmapLevelDataSO self, in BeatmapKey beatmapKey);

        private BeatmapJsonCacheHooks(BeatmapLevelCache beatmapLevelCache)
        {
            _beatmapLevelCache = beatmapLevelCache;
        }

        public void Initialize()
        {
            _getAudioStringAsyncHook = new Hook(typeof(FileSystemBeatmapLevelData).GetMethod(nameof(FileSystemBeatmapLevelData.GetAudioDataStringAsync))!, GetAudioDataJsonAsync, true);
            _getBeatmapStringAsyncHook = new Hook(typeof(FileSystemBeatmapLevelData).GetMethod(nameof(FileSystemBeatmapLevelData.GetBeatmapStringAsync))!, GetBeatmapDataJsonAsync, true);
            _getLightshowStringAsyncHook = new Hook(typeof(FileSystemBeatmapLevelData).GetMethod(nameof(FileSystemBeatmapLevelData.GetLightshowStringAsync))!, GetLightshowDataJsonAsync, true);
            _getAudioStringHook = new Hook(typeof(FileSystemBeatmapLevelData).GetMethod(nameof(FileSystemBeatmapLevelData.GetAudioDataString))!, GetAudioDataJson, true);
            _getBeatmapStringHook = new Hook(typeof(FileSystemBeatmapLevelData).GetMethod(nameof(FileSystemBeatmapLevelData.GetBeatmapString))!, GetBeatmapDataJson, true);
            _getLightshowStringHook = new Hook(typeof(FileSystemBeatmapLevelData).GetMethod(nameof(FileSystemBeatmapLevelData.GetLightshowString))!, GetLightshowDataJson, true);
            _getOstAudioStringAsyncHook = new Hook(typeof(BeatmapLevelDataSO).GetMethod(nameof(BeatmapLevelDataSO.GetAudioDataStringAsync))!, GetOstAudioDataJsonAsync, true);
            _getOstBeatmapStringAsyncHook = new Hook(typeof(BeatmapLevelDataSO).GetMethod(nameof(BeatmapLevelDataSO.GetBeatmapStringAsync))!, GetOstBeatmapDataJsonAsync, true);
            _getOstLightshowStringAsyncHook = new Hook(typeof(BeatmapLevelDataSO).GetMethod(nameof(BeatmapLevelDataSO.GetLightshowStringAsync))!, GetOstLightshowDataJsonAsync, true);
            _getOstAudioStringHook = new Hook(typeof(BeatmapLevelDataSO).GetMethod(nameof(BeatmapLevelDataSO.GetAudioDataString))!, GetOstAudioDataJson, true);
            _getOstBeatmapStringHook = new Hook(typeof(BeatmapLevelDataSO).GetMethod(nameof(BeatmapLevelDataSO.GetBeatmapString))!, GetOstBeatmapDataJson, true);
            _getOstLightshowStringHook = new Hook(typeof(BeatmapLevelDataSO).GetMethod(nameof(BeatmapLevelDataSO.GetLightshowString))!, GetOstLightshowDataJson, true);
        }

        public void Dispose()
        {
            _getAudioStringAsyncHook.Dispose();
            _getBeatmapStringAsyncHook.Dispose();
            _getLightshowStringAsyncHook.Dispose();
            _getAudioStringHook.Dispose();
            _getBeatmapStringHook.Dispose();
            _getLightshowStringHook.Dispose();
            _getOstAudioStringAsyncHook.Dispose();
            _getOstBeatmapStringAsyncHook.Dispose();
            _getOstLightshowStringAsyncHook.Dispose();
            _getOstAudioStringHook.Dispose();
            _getOstBeatmapStringHook.Dispose();
            _getOstLightshowStringHook.Dispose();
        }

        private Task<string?> GetAudioDataJsonAsync(Func<FileSystemBeatmapLevelData, Task<string?>> original, FileSystemBeatmapLevelData instance)
        {
            return _beatmapLevelCache.GetJsonDataAsync(instance, new BeatmapKey(), BeatmapDataType.Audio, () => original(instance));
        }

        private Task<string?> GetOstAudioDataJsonAsync(Func<BeatmapLevelDataSO, Task<string?>> original, BeatmapLevelDataSO instance)
        {
            return _beatmapLevelCache.GetJsonDataAsync(instance, new BeatmapKey(), BeatmapDataType.Audio, () => original(instance));
        }

        private Task<string?> GetBeatmapDataJsonAsync(GetStringAsyncDelegate original, FileSystemBeatmapLevelData instance, in BeatmapKey beatmapKey)
        {
            var key = beatmapKey;
            return _beatmapLevelCache.GetJsonDataAsync(instance, beatmapKey, BeatmapDataType.Beatmap, () => original(instance, key));
        }

        private Task<string?> GetOstBeatmapDataJsonAsync(GetOstStringAsyncDelegate original, BeatmapLevelDataSO instance, in BeatmapKey beatmapKey)
        {
            var key = beatmapKey;
            return _beatmapLevelCache.GetJsonDataAsync(instance, beatmapKey, BeatmapDataType.Beatmap, () => original(instance, key));
        }

        private Task<string?> GetLightshowDataJsonAsync(GetStringAsyncDelegate original, FileSystemBeatmapLevelData instance, in BeatmapKey beatmapKey)
        {
            var key = beatmapKey;
            return _beatmapLevelCache.GetJsonDataAsync(instance, beatmapKey, BeatmapDataType.Lightshow, () => original(instance, key));
        }

        private Task<string?> GetOstLightshowDataJsonAsync(GetOstStringAsyncDelegate original, BeatmapLevelDataSO instance, in BeatmapKey beatmapKey)
        {
            var key = beatmapKey;
            return _beatmapLevelCache.GetJsonDataAsync(instance, beatmapKey, BeatmapDataType.Lightshow, () => original(instance, key));
        }

        private string? GetAudioDataJson(Func<FileSystemBeatmapLevelData, string?> original, FileSystemBeatmapLevelData instance)
        {
            return _beatmapLevelCache.GetJsonData(instance, new BeatmapKey(), BeatmapDataType.Audio, () => original(instance));
        }

        private string? GetOstAudioDataJson(Func<BeatmapLevelDataSO, string?> original, BeatmapLevelDataSO instance)
        {
            return _beatmapLevelCache.GetJsonData(instance, new BeatmapKey(), BeatmapDataType.Audio, () => original(instance));
        }

        private string? GetBeatmapDataJson(GetStringDelegate original, FileSystemBeatmapLevelData instance, in BeatmapKey beatmapKey)
        {
            var key = beatmapKey;
            return _beatmapLevelCache.GetJsonData(instance, beatmapKey, BeatmapDataType.Beatmap, () => original(instance, key));
        }

        private string? GetOstBeatmapDataJson(GetOstStringDelegate original, BeatmapLevelDataSO instance, in BeatmapKey beatmapKey)
        {
            var key = beatmapKey;
            return _beatmapLevelCache.GetJsonData(instance, beatmapKey, BeatmapDataType.Beatmap, () => original(instance, key));
        }

        private string? GetLightshowDataJson(GetStringDelegate original, FileSystemBeatmapLevelData instance, in BeatmapKey beatmapKey)
        {
            var key = beatmapKey;
            return _beatmapLevelCache.GetJsonData(instance, beatmapKey, BeatmapDataType.Lightshow, () => original(instance, key));
        }

        private string? GetOstLightshowDataJson(GetOstStringDelegate original, BeatmapLevelDataSO instance, in BeatmapKey beatmapKey)
        {
            var key = beatmapKey;
            return _beatmapLevelCache.GetJsonData(instance, beatmapKey, BeatmapDataType.Lightshow, () => original(instance, key));
        }
    }
}

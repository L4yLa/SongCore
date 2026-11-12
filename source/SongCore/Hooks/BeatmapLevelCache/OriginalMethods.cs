namespace SongCore.Hooks.BeatmapLevelCache
{
    /// <summary>
    /// The way to read beatmap files around the cache in 3.16.0, kept for mods built against it. Nothing is cached any
    /// more, so these call the game directly.
    /// </summary>
    internal static class OriginalMethods
    {
        public static class FileSystemBeatmapLevelData
        {
            public static string? GetAudioDataString(global::FileSystemBeatmapLevelData instance)
            {
                return instance.GetAudioDataString();
            }

            public static string? GetBeatmapString(global::FileSystemBeatmapLevelData instance, in BeatmapKey beatmapKey)
            {
                return instance.GetBeatmapString(in beatmapKey);
            }

            public static string? GetLightshowString(global::FileSystemBeatmapLevelData instance, in BeatmapKey beatmapKey)
            {
                return instance.GetLightshowString(in beatmapKey);
            }
        }
    }
}

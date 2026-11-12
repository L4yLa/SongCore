using System;
using System.Collections.Concurrent;
using System.Threading;
using Zenject;

namespace SongCore.Hooks.BeatmapLevelCache
{
    /// <summary>
    /// The file access blacklist of 3.16.0, kept for mods built against it. In 3.16.0, reading the files of a loaded
    /// beatmap directly threw unless <see cref="AllowIO"/> was set, so that the cached JSON had to be used. Nothing is
    /// cached any more, so nothing is blocked: the blacklist stays empty and <see cref="AllowIO"/> has no effect.
    /// </summary>
    internal class IOBlacklistHooks : IInitializable, IDisposable
    {
        public static ConcurrentDictionary<string, string> FilesBlacklist { get; } = new ConcurrentDictionary<string, string>();
        public static AsyncLocal<bool> AllowIO { get; } = new AsyncLocal<bool>();

        public void Initialize()
        {
        }

        public void Dispose()
        {
        }
    }
}

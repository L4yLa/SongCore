using System.Runtime.CompilerServices;
using IPA.Config.Stores;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]
namespace SongCore
{
    internal class PluginConfig
    {
        public virtual bool CustomSongNoteColors { get; set; } = true;
        public virtual bool CustomSongObstacleColors { get; set; } = true;
        public virtual bool CustomSongEnvironmentColors { get; set; } = true;
        public virtual bool DisplayDiffLabels { get; set; } = true;
        public virtual bool DisplayCustomCharacteristics { get; set; } = true;
        public virtual bool ForceLongPreviews { get; set; } = true;
        public virtual bool DisableRotationSpawnLinesOverride { get; set; }
        public virtual bool DisableOneSaberOverride { get; set; }

        /// <summary>
        /// Logs every duplicate beatmap on its own line instead of one line per group of duplicates.
        /// Only configurable through the config file, as the verbose output is intended for diagnostics
        /// and is large enough that the log subsystem may start dropping messages on big libraries.
        /// </summary>
        public virtual bool VerboseDuplicateLogging { get; set; }
    }
}
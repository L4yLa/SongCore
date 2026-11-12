using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;

namespace SongCore.UI
{
    internal class SettingsController : NotifiableBase
    {
        private readonly PluginConfig _config;

        private SettingsController(PluginConfig config)
        {
            _config = config;
        }

        [UIValue("noteColors")]
        public bool NoteColors
        {
            get => _config.CustomSongNoteColors;
            set
            {
                _config.CustomSongNoteColors = value;
                NotifyPropertyChanged();
            }
        }

        [UIValue("obstacleColors")]
        public bool ObstacleColors
        {
            get => _config.CustomSongObstacleColors;
            set
            {
                _config.CustomSongObstacleColors = value;
                NotifyPropertyChanged();
            }
        }

        [UIValue("environmentColors")]
        public bool EnvironmentColors
        {
            get => _config.CustomSongEnvironmentColors;
            set
            {
                _config.CustomSongEnvironmentColors = value;
                NotifyPropertyChanged();
            }
        }

        [UIValue("diffLabels")]
        public bool DiffLabels
        {
            get => _config.DisplayDiffLabels;
            set => _config.DisplayDiffLabels = value;
        }

        [UIValue("customChara")]
        public bool CustomChara
        {
            get => _config.DisplayCustomCharacteristics;
            set => _config.DisplayCustomCharacteristics = value;
        }

        [UIValue("longPreviews")]
        public bool LongPreviews
        {
            get => _config.ForceLongPreviews;
            set => _config.ForceLongPreviews = value;
        }

        [UIValue("spawnlines")]
        public bool SpawnLines
        {
            get => _config.DisableRotationSpawnLinesOverride;
            set => _config.DisableRotationSpawnLinesOverride = value;
        }

        [UIValue("over100")]
        public bool BlockOver100PercentDifficulties
        {
            get => _config.BlockOver100PercentDifficulties;
            set => _config.BlockOver100PercentDifficulties = value;
        }
    }
}

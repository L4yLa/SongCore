using System;
using System.Linq;
using SongCore.Data;

namespace SongCore.Utilities
{
    internal static class SongDataExtensions
    {
        /// <summary>
        /// Converts song data into the type of the old API. Everything is copied, including the arrays, so that a
        /// mod changing what it was given cannot change the song data held by SongCore.
        /// </summary>
        [Obsolete]
        public static ExtraSongData ToExtraSongData(this SongData songData)
        {
            var contributors = songData.contributors.Select(c => new ExtraSongData.Contributor
            {
                _role = c._role,
                _name = c._name,
                _iconPath = c._iconPath,
                icon = c.icon
            }).ToArray();

            var difficulties = songData._difficulties.Select(d => new ExtraSongData.DifficultyData
            {
                _beatmapCharacteristicName = d._beatmapCharacteristicName,
                _difficulty = d._difficulty,
                _difficultyLabel = d._difficultyLabel,
                additionalDifficultyData = new ExtraSongData.RequirementData
                {
                    _requirements = d.additionalDifficultyData._requirements.ToArray(),
                    _suggestions = d.additionalDifficultyData._suggestions.ToArray(),
                    _warnings = d.additionalDifficultyData._warnings.ToArray(),
                    _information = d.additionalDifficultyData._information.ToArray()
                },
                _colorLeft = ToMapColor(d._colorLeft),
                _colorRight = ToMapColor(d._colorRight),
                _envColorLeft = ToMapColor(d._envColorLeft),
                _envColorRight = ToMapColor(d._envColorRight),
                _envColorWhite = ToMapColor(d._envColorWhite),
                _envColorLeftBoost = ToMapColor(d._envColorLeftBoost),
                _envColorRightBoost = ToMapColor(d._envColorRightBoost),
                _envColorWhiteBoost = ToMapColor(d._envColorWhiteBoost),
                _obstacleColor = ToMapColor(d._obstacleColor),
                _beatmapColorSchemeIdx = d._beatmapColorSchemeIdx,
                _environmentNameIdx = d._environmentNameIdx,
                _oneSaber = d._oneSaber,
                _showRotationNoteSpawnLines = d._showRotationNoteSpawnLines,
                _styleTags = d._styleTags.ToArray()
            }).ToArray();

            // Entries that could not be read are kept as placeholders without an override, so that the indices
            // used by _beatmapColorSchemeIdx keep pointing at the right scheme.
            var colorSchemes = songData._colorSchemes.Select(c => new ExtraSongData.ColorScheme
            {
                useOverride = c.useOverride,
                colorSchemeId = c.colorSchemeId,
                saberAColor = ToMapColor(c.saberAColor),
                saberBColor = ToMapColor(c.saberBColor),
                environmentColor0 = ToMapColor(c.environmentColor0),
                environmentColor1 = ToMapColor(c.environmentColor1),
                obstaclesColor = ToMapColor(c.obstaclesColor),
                environmentColor0Boost = ToMapColor(c.environmentColor0Boost),
                environmentColor1Boost = ToMapColor(c.environmentColor1Boost),
                environmentColorW = ToMapColor(c.environmentColorW),
                environmentColorWBoost = ToMapColor(c.environmentColorWBoost)
            }).ToArray();

            // Null and empty mean different things here, so null is passed through as-is.
            var characteristicDetails = songData._characteristicDetails?.Select(detail => new ExtraSongData.CharacteristicDetails
            {
                _beatmapCharacteristicName = detail._beatmapCharacteristicName,
                _characteristicLabel = detail._characteristicLabel,
                _characteristicIconFilePath = detail._characteristicIconFilePath
            }).ToArray();

            return new ExtraSongData
            {
                _genreTags = songData._genreTags.ToArray(),
                contributors = contributors,
                _customEnvironmentName = songData._customEnvironmentName,
                _customEnvironmentHash = songData._customEnvironmentHash,
                _difficulties = difficulties,
                _defaultCharacteristic = songData._defaultCharacteristic,
                _colorSchemes = colorSchemes,
                _environmentNames = songData._environmentNames.ToArray(),
                _characteristicDetails = characteristicDetails
            };
        }

        [Obsolete]
        private static ExtraSongData.MapColor? ToMapColor(SongData.MapColor? color)
        {
            return color != null ? new ExtraSongData.MapColor(color.r, color.g, color.b, color.a) : null;
        }
    }
}

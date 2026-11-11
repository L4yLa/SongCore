using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace SongCore.Data
{
    [Serializable]
    public class SongData
    {
        /// <summary>
        /// Version of the way song data is read from the info file. Cached song data of an older version is
        /// read again, so that changes to the validation also apply to beatmaps that did not change.
        /// </summary>
        internal const int CurrentDataVersion = 1;

        public int _dataVersion;

        // Hash of the beatmap folder this data was built from. Cached data is discarded when the folder
        // no longer hashes to this value, so edits to an info file are picked up on the next refresh.
        public long directoryHash;

        public string[] _genreTags = Array.Empty<string>();
        public Contributor[] contributors = Array.Empty<Contributor>(); //convert legacy mappers/lighters fields into contributors
        public string? _customEnvironmentName;
        public string? _customEnvironmentHash;
        public DifficultyData[] _difficulties = Array.Empty<DifficultyData>();
        public string? _defaultCharacteristic;

        public ColorScheme[] _colorSchemes = Array.Empty<ColorScheme>(); //beatmap 2.1.0, community decided to song-core ify colour stuff
        public string[] _environmentNames = Array.Empty<string>(); //these have underscores but the actual format doesnt, I genuinely dont know what to go by so I went consistent with songcore

        //PinkCore Port
        public CharacteristicDetails[]? _characteristicDetails;

        /// <summary>
        /// Problems in the info file that affect the whole beatmap and make it unplayable.
        /// Problems that only affect one difficulty are stored on that difficulty.
        /// </summary>
        public string[] _validationErrors = Array.Empty<string>();

        /// <summary>
        /// Problems in the info file that do not affect playing, such as unreadable colors or contributors.
        /// </summary>
        public string[] _validationWarnings = Array.Empty<string>();

        [Serializable]
        public class CharacteristicDetails
        {
            public string _beatmapCharacteristicName = string.Empty;
            public string? _characteristicLabel;
            public string? _characteristicIconFilePath = null;
        }


        [Serializable]
        public class Contributor
        {
            public string _role = string.Empty;
            public string _name = string.Empty;
            public string? _iconPath;

            [NonSerialized]
            public Sprite? icon = null;
        }

        [Serializable]
        public class DifficultyData
        {
            public string _beatmapCharacteristicName = string.Empty;
            public BeatmapDifficulty _difficulty;
            public string _difficultyLabel = string.Empty;
            public RequirementData additionalDifficultyData = new RequirementData();
            public MapColor? _colorLeft;
            public MapColor? _colorRight;
            public MapColor? _envColorLeft;
            public MapColor? _envColorRight;
            public MapColor? _envColorWhite;
            public MapColor? _envColorLeftBoost;
            public MapColor? _envColorRightBoost;
            public MapColor? _envColorWhiteBoost;
            public MapColor? _obstacleColor;
            public int? _beatmapColorSchemeIdx;
            public int? _environmentNameIdx;

            //PinkCore Port
            public bool? _oneSaber;
            public bool? _showRotationNoteSpawnLines;
            //Tags
            public string[] _styleTags = Array.Empty<string>();

            /// <summary>
            /// Problems in the info file that make this difficulty unplayable.
            /// </summary>
            public string[] _validationErrors = Array.Empty<string>();

            /// <summary>
            /// Problems in the info file that do not affect playing this difficulty.
            /// </summary>
            public string[] _validationWarnings = Array.Empty<string>();
        }

        [Serializable]
        public class ColorScheme //stuck to the same naming convention as the json itself
        {
            public bool useOverride;
            public string? colorSchemeId;
            public MapColor? saberAColor;
            public MapColor? saberBColor;
            public MapColor? environmentColor0;
            public MapColor? environmentColor1;
            public MapColor? obstaclesColor;
            public MapColor? environmentColor0Boost;
            public MapColor? environmentColor1Boost;
            //Not officially within the default scheme, added for consistency
            public MapColor? environmentColorW;
            public MapColor? environmentColorWBoost;
        }


        [Serializable]
        public class RequirementData
        {
            public string[] _requirements = Array.Empty<string>();
            public string[] _suggestions = Array.Empty<string>();
            public string[] _warnings = Array.Empty<string>();
            public string[] _information = Array.Empty<string>();
        }

        [Serializable]
        public class MapColor
        {
            public float r;
            public float g;
            public float b;

            [DefaultValue(1)]
            [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
            public float a = 1f;

            public MapColor(float r, float g, float b, float a = 1f)
            {
                this.r = r;
                this.g = g;
                this.b = b;
                this.a = a;
            }
        }

        public SongData()
        {
        }

        [JsonConstructor]
        public SongData(string? levelID, Contributor[]? contributors, string? customEnvironmentName, string? customEnvironmentHash, DifficultyData[]? difficulties)
        {
            this.contributors = contributors ?? Array.Empty<Contributor>();
            _customEnvironmentName = customEnvironmentName;
            _customEnvironmentHash = customEnvironmentHash;
            _difficulties = difficulties ?? Array.Empty<DifficultyData>();
        }

        /// <summary>
        /// Whether the given difficulty can be played as far as the info file is concerned.
        /// </summary>
        public bool HasValidationErrors(DifficultyData? difficultyData)
        {
            return _validationErrors.Length > 0 || difficultyData is { _validationErrors.Length: > 0 };
        }

        internal void PopulateFromLoadedSaveData(CustomLevelLoader.LoadedSaveData loadedSaveData)
        {
            _dataVersion = CurrentDataVersion;

            var folderPath = loadedSaveData.customLevelFolderInfo.folderPath;
            var report = new ValidationReport();
            try
            {
                var levelInfo = JObject.Parse(loadedSaveData.customLevelFolderInfo.levelInfoJsonString);
                if (loadedSaveData.standardLevelInfoSaveData != null)
                {
                    PopulateFromInfoV2(levelInfo, folderPath, report);
                }
                else if (loadedSaveData.beatmapLevelSaveData != null)
                {
                    PopulateFromInfoV4(levelInfo, folderPath, report);
                }
                else
                {
                    report.SongError("The info file could not be read by the game.");
                }
            }
            catch (Exception ex)
            {
                // The readers below do not throw for malformed values, so this is either invalid JSON or a bug.
                // Requirements cannot be known in either case, so the beatmap is not playable.
                report.SongError($"The info file could not be read: {ex.Message}");
                Plugin.Log.Debug(ex.ToString());
            }

            _validationErrors = report.SongErrors.ToArray();
            _validationWarnings = report.SongWarnings.ToArray();
            report.ApplyTo(_difficulties);
            Normalize();

            if (report.ErrorCount > 0)
            {
                Plugin.Log.Warn($"Beatmap {folderPath} is not playable: {string.Join("; ", report.AllErrors())}");
            }

            if (report.WarningCount > 0)
            {
                Plugin.Log.Debug($"Beatmap {folderPath} has problems in its info file: {string.Join("; ", report.AllWarnings())}");
            }
        }

        private void PopulateFromInfoV2(JObject levelInfo, string folderPath, ValidationReport report)
        {
            const string info = "Info.dat";

            var customData = InfoReader.GetObject(levelInfo, "_customData", info, report.SongWarning);
            if (customData != null)
            {
                contributors = InfoReader.GetArray(customData, "_contributors", info, report.SongWarning)
                    .Select(t => ReadContributorV2(t, report))
                    .OfType<Contributor>()
                    .ToArray();
                _genreTags = InfoReader.GetStringArray(customData, "_genreTags", info, report.SongWarning);
                _customEnvironmentName = InfoReader.GetString(customData, "_customEnvironment", info, report.SongWarning);
                _customEnvironmentHash = InfoReader.GetString(customData, "_customEnvironmentHash", info, report.SongWarning);
                _defaultCharacteristic = InfoReader.GetString(customData, "_defaultCharacteristic", info, report.SongWarning);
            }

            _environmentNames = InfoReader.GetStringArray(levelInfo, "_environmentNames", info, report.SongWarning);
            _colorSchemes = ReadColorSchemes(levelInfo, "_colorSchemes", info, report);

            var difficultyBeatmapSets = InfoReader.GetArray(levelInfo, "_difficultyBeatmapSets", info, report.SongError, required: true);
            var difficultyData = new List<DifficultyData>();
            var characteristicsDetails = new List<CharacteristicDetails>();

            foreach (var difficultyBeatmapSetToken in difficultyBeatmapSets)
            {
                if (difficultyBeatmapSetToken is not JObject difficultyBeatmapSet)
                {
                    report.SongError($"{info}: an entry of '_difficultyBeatmapSets' is not an object.");
                    continue;
                }

                var beatmapCharacteristicName = InfoReader.GetString(difficultyBeatmapSet, "_beatmapCharacteristicName", info, report.SongWarning);
                if (string.IsNullOrEmpty(beatmapCharacteristicName))
                {
                    // The game shows these as a missing characteristic, which is already not playable.
                    report.SongWarning($"{info}: a difficulty set has no characteristic name.");
                    continue;
                }

                var setLocation = beatmapCharacteristicName!;
                var customCharacteristicData = InfoReader.GetObject(difficultyBeatmapSet, "_customData", setLocation, report.SongWarning);
                if (customCharacteristicData != null)
                {
                    characteristicsDetails.Add(new CharacteristicDetails
                    {
                        _beatmapCharacteristicName = beatmapCharacteristicName!,
                        _characteristicLabel = InfoReader.GetString(customCharacteristicData, "_characteristicLabel", setLocation, report.SongWarning),
                        _characteristicIconFilePath = InfoReader.GetString(customCharacteristicData, "_characteristicIconImageFilename", setLocation, report.SongWarning)
                    });
                }

                var difficultyBeatmaps = InfoReader.GetArray(difficultyBeatmapSet, "_difficultyBeatmaps", setLocation, report.SongError, required: true);
                foreach (var difficultyBeatmapToken in difficultyBeatmaps)
                {
                    if (difficultyBeatmapToken is not JObject difficultyBeatmap)
                    {
                        report.SongError($"{setLocation}: an entry of '_difficultyBeatmaps' is not an object.");
                        continue;
                    }

                    var difficultyName = InfoReader.GetString(difficultyBeatmap, "_difficulty", setLocation, report.SongWarning);
                    var difficultyErrors = new List<string>();
                    var difficultyWarnings = new List<string>();

                    // The game reads the difficulty name with this method as well. For this info file version it
                    // falls back to Normal for unknown names, so such a difficulty is played and scored as Normal.
                    if (!BeatmapDifficultySerializedMethods.BeatmapDifficultyFromSerializedName(difficultyName ?? string.Empty, out var difficulty))
                    {
                        difficulty = BeatmapDifficulty.Normal;
                        difficultyErrors.Add($"Unknown difficulty name '{difficultyName}', which the game treats as Normal.");
                    }

                    var beatmapFilename = InfoReader.GetString(difficultyBeatmap, "_beatmapFilename", string.Empty, difficultyErrors.Add);
                    CheckFileExists(folderPath, beatmapFilename, "_beatmapFilename", string.Empty, difficultyErrors.Add);

                    var data = ReadDifficulty(levelInfo, difficultyBeatmap, beatmapCharacteristicName!, difficulty, isV4: false, difficultyErrors, difficultyWarnings);
                    AddDifficulty(difficultyData, data, report);
                }
            }

            _difficulties = difficultyData.ToArray();
            _characteristicDetails = characteristicsDetails.ToArray();
        }

        private void PopulateFromInfoV4(JObject levelInfo, string folderPath, ValidationReport report)
        {
            const string info = "Info.dat";

            var customData = InfoReader.GetObject(levelInfo, "customData", info, report.SongWarning);
            if (customData != null)
            {
                contributors = InfoReader.GetArray(customData, "contributors", info, report.SongWarning)
                    .Select(t => ReadContributorV4(t, report))
                    .OfType<Contributor>()
                    .ToArray();
                _genreTags = InfoReader.GetStringArray(customData, "genreTags", info, report.SongWarning);
                _customEnvironmentName = InfoReader.GetString(customData, "customEnvironment", info, report.SongWarning);
                _customEnvironmentHash = InfoReader.GetString(customData, "customEnvironmentHash", info, report.SongWarning);
                _defaultCharacteristic = InfoReader.GetString(customData, "defaultCharacteristic", info, report.SongWarning);

                if (customData.ContainsKey("characteristicData"))
                {
                    _characteristicDetails = InfoReader.GetArray(customData, "characteristicData", info, report.SongWarning)
                        .Select(t =>
                        {
                            if (t is not JObject characteristic)
                            {
                                report.SongWarning($"{info}: an entry of 'characteristicData' is not an object.");
                                return null;
                            }

                            var name = InfoReader.GetString(characteristic, "characteristic", info, report.SongWarning);
                            if (string.IsNullOrEmpty(name))
                            {
                                report.SongWarning($"{info}: an entry of 'characteristicData' has no characteristic name.");
                                return null;
                            }

                            return new CharacteristicDetails
                            {
                                _beatmapCharacteristicName = name!,
                                _characteristicLabel = InfoReader.GetString(characteristic, "label", info, report.SongWarning),
                                _characteristicIconFilePath = InfoReader.GetString(characteristic, "iconPath", info, report.SongWarning)
                            };
                        })
                        .OfType<CharacteristicDetails>()
                        .ToArray();
                }
            }
            else
            {
                _characteristicDetails = Array.Empty<CharacteristicDetails>();
            }

            _environmentNames = InfoReader.GetStringArray(levelInfo, "environmentNames", info, report.SongWarning);
            _colorSchemes = ReadColorSchemes(levelInfo, "colorSchemes", info, report);

            var difficultyBeatmaps = InfoReader.GetArray(levelInfo, "difficultyBeatmaps", info, report.SongError, required: true);
            var difficultyData = new List<DifficultyData>();

            foreach (var difficultyBeatmapToken in difficultyBeatmaps)
            {
                if (difficultyBeatmapToken is not JObject difficultyBeatmap)
                {
                    report.SongError($"{info}: an entry of 'difficultyBeatmaps' is not an object.");
                    continue;
                }

                var beatmapCharacteristicName = InfoReader.GetString(difficultyBeatmap, "characteristic", info, report.SongWarning);
                if (string.IsNullOrEmpty(beatmapCharacteristicName))
                {
                    report.SongWarning($"{info}: a difficulty has no characteristic name.");
                    continue;
                }

                var difficultyName = InfoReader.GetString(difficultyBeatmap, "difficulty", beatmapCharacteristicName!, report.SongWarning);

                // The game skips difficulties with an unknown name for this info file version, so there is no
                // playable difficulty to block. It is only reported.
                if (!BeatmapDifficultySerializedMethods.BeatmapDifficultyFromSerializedName(difficultyName ?? string.Empty, out var difficulty))
                {
                    report.SongWarning($"{beatmapCharacteristicName}: unknown difficulty name '{difficultyName}', which the game ignores.");
                    continue;
                }

                var difficultyErrors = new List<string>();
                var difficultyWarnings = new List<string>();
                var beatmapDataFilename = InfoReader.GetString(difficultyBeatmap, "beatmapDataFilename", string.Empty, difficultyErrors.Add);
                CheckFileExists(folderPath, beatmapDataFilename, "beatmapDataFilename", string.Empty, difficultyErrors.Add);
                var lightshowDataFilename = InfoReader.GetString(difficultyBeatmap, "lightshowDataFilename", string.Empty, difficultyWarnings.Add);
                CheckFileExists(folderPath, lightshowDataFilename, "lightshowDataFilename", string.Empty, difficultyWarnings.Add);

                var data = ReadDifficulty(levelInfo, difficultyBeatmap, beatmapCharacteristicName!, difficulty, isV4: true, difficultyErrors, difficultyWarnings);
                AddDifficulty(difficultyData, data, report);
            }

            _difficulties = difficultyData.ToArray();
        }

        private DifficultyData ReadDifficulty(JObject levelInfo, JObject difficultyBeatmap, string beatmapCharacteristicName, BeatmapDifficulty difficulty, bool isV4,
            List<string> errors, List<string> warnings)
        {
            const string location = "";

            string Key(string v2Key) => isV4 ? char.ToLowerInvariant(v2Key[1]) + v2Key.Substring(2) : v2Key;

            var data = new DifficultyData
            {
                _beatmapCharacteristicName = beatmapCharacteristicName,
                _difficulty = difficulty,
                _beatmapColorSchemeIdx = InfoReader.GetValue<int>(difficultyBeatmap, Key("_beatmapColorSchemeIdx"), location, warnings.Add),
                _environmentNameIdx = InfoReader.GetValue<int>(difficultyBeatmap, Key("_environmentNameIdx"), location, warnings.Add)
            };

            var useSongCoreColors = true;
            if (data._beatmapColorSchemeIdx != null)
            {
                var colorScheme = _colorSchemes.ElementAtOrDefault(data._beatmapColorSchemeIdx.Value);
                if (colorScheme is { useOverride: true })
                {
                    useSongCoreColors = false;
                    data._colorLeft = colorScheme.saberAColor;
                    data._colorRight = colorScheme.saberBColor;
                    data._envColorLeft = colorScheme.environmentColor0;
                    data._envColorRight = colorScheme.environmentColor1;
                    data._envColorWhite = colorScheme.environmentColorW;
                    data._envColorLeftBoost = colorScheme.environmentColor0Boost;
                    data._envColorRightBoost = colorScheme.environmentColor1Boost;
                    data._envColorWhiteBoost = colorScheme.environmentColorWBoost;
                    data._obstacleColor = colorScheme.obstaclesColor;
                }
            }

            // Requirements are in here, so a custom data entry that cannot be read means it is unknown which
            // mods the difficulty needs.
            var customData = InfoReader.GetObject(difficultyBeatmap, Key("_customData"), location, errors.Add);
            if (customData != null)
            {
                data._styleTags = InfoReader.GetStringArray(levelInfo, Key("_styleTags"), location, warnings.Add);
                data.additionalDifficultyData = new RequirementData
                {
                    _requirements = InfoReader.GetStringArray(customData, Key("_requirements"), location, errors.Add),
                    _suggestions = InfoReader.GetStringArray(customData, Key("_suggestions"), location, warnings.Add),
                    _warnings = InfoReader.GetStringArray(customData, Key("_warnings"), location, warnings.Add),
                    _information = InfoReader.GetStringArray(customData, Key("_information"), location, warnings.Add)
                };
                data._difficultyLabel = InfoReader.GetString(customData, Key("_difficultyLabel"), location, warnings.Add) ?? string.Empty;
                // Both change how the difficulty is played, so they have to be readable.
                data._oneSaber = InfoReader.GetValue<bool>(customData, Key("_oneSaber"), location, errors.Add);
                data._showRotationNoteSpawnLines = InfoReader.GetValue<bool>(customData, Key("_showRotationNoteSpawnLines"), location, errors.Add);

                if (useSongCoreColors)
                {
                    data._colorLeft = InfoReader.GetColor(customData, Key("_colorLeft"), location, warnings.Add);
                    data._colorRight = InfoReader.GetColor(customData, Key("_colorRight"), location, warnings.Add);
                    data._envColorLeft = InfoReader.GetColor(customData, Key("_envColorLeft"), location, warnings.Add);
                    data._envColorRight = InfoReader.GetColor(customData, Key("_envColorRight"), location, warnings.Add);
                    data._envColorWhite = InfoReader.GetColor(customData, Key("_envColorWhite"), location, warnings.Add);
                    data._envColorLeftBoost = InfoReader.GetColor(customData, Key("_envColorLeftBoost"), location, warnings.Add);
                    data._envColorRightBoost = InfoReader.GetColor(customData, Key("_envColorRightBoost"), location, warnings.Add);
                    data._envColorWhiteBoost = InfoReader.GetColor(customData, Key("_envColorWhiteBoost"), location, warnings.Add);
                    data._obstacleColor = InfoReader.GetColor(customData, Key("_obstacleColor"), location, warnings.Add);
                }
            }

            data._validationErrors = errors.ToArray();
            data._validationWarnings = warnings.ToArray();
            return data;
        }

        /// <summary>
        /// Adds a difficulty, reporting a second difficulty with the same characteristic and difficulty. The
        /// game cannot tell such difficulties apart, so it is unknown which one is played and scored.
        /// </summary>
        private static void AddDifficulty(List<DifficultyData> difficulties, DifficultyData data, ValidationReport report)
        {
            var existing = difficulties.FirstOrDefault(d => d._difficulty == data._difficulty && d._beatmapCharacteristicName == data._beatmapCharacteristicName);
            if (existing == null)
            {
                difficulties.Add(data);
                return;
            }

            report.AddDifficultyError(existing, "The info file contains this difficulty more than once.");
        }

        private static void CheckFileExists(string folderPath, string? fileName, string key, string location, Action<string> report)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                report($"{InfoReader.Prefix(location)}'{key}' is missing.");
                return;
            }

            try
            {
                if (!File.Exists(Path.Combine(folderPath, fileName)))
                {
                    report($"{InfoReader.Prefix(location)}The file '{fileName}' does not exist.");
                }
            }
            catch (ArgumentException)
            {
                report($"{InfoReader.Prefix(location)}'{fileName}' is not a valid file name.");
            }
        }

        private static Contributor? ReadContributorV2(JToken token, ValidationReport report)
        {
            if (token is not JObject contributor)
            {
                report.SongWarning("Info.dat: an entry of '_contributors' is not an object.");
                return null;
            }

            return new Contributor
            {
                _role = InfoReader.GetString(contributor, "_role", "Info.dat", report.SongWarning) ?? string.Empty,
                _name = InfoReader.GetString(contributor, "_name", "Info.dat", report.SongWarning) ?? string.Empty,
                _iconPath = InfoReader.GetString(contributor, "_iconPath", "Info.dat", report.SongWarning)
            };
        }

        private static Contributor? ReadContributorV4(JToken token, ValidationReport report)
        {
            if (token is not JObject contributor)
            {
                report.SongWarning("Info.dat: an entry of 'contributors' is not an object.");
                return null;
            }

            return new Contributor
            {
                _role = InfoReader.GetString(contributor, "role", "Info.dat", report.SongWarning) ?? string.Empty,
                _name = InfoReader.GetString(contributor, "name", "Info.dat", report.SongWarning) ?? string.Empty,
                _iconPath = InfoReader.GetString(contributor, "iconPath", "Info.dat", report.SongWarning)
            };
        }

        /// <summary>
        /// Reads the color schemes. Entries that cannot be read are kept as schemes without an override, so that
        /// the indices used by the difficulties keep pointing at the right scheme.
        /// </summary>
        private static ColorScheme[] ReadColorSchemes(JObject levelInfo, string key, string location, ValidationReport report)
        {
            return InfoReader.GetArray(levelInfo, key, location, report.SongWarning)
                .Select(c =>
                {
                    if (c is not JObject colorSchemeSaveData)
                    {
                        report.SongWarning($"{InfoReader.Prefix(location)}an entry of '{key}' is not an object.");
                        return new ColorScheme();
                    }

                    var colorScheme = InfoReader.GetObject(colorSchemeSaveData, "colorScheme", location, report.SongWarning);
                    if (colorScheme == null)
                    {
                        return new ColorScheme();
                    }

                    return new ColorScheme
                    {
                        useOverride = InfoReader.GetValue<bool>(colorSchemeSaveData, "useOverride", location, report.SongWarning) ?? false,
                        saberAColor = InfoReader.GetColor(colorScheme, "saberAColor", location, report.SongWarning),
                        saberBColor = InfoReader.GetColor(colorScheme, "saberBColor", location, report.SongWarning),
                        environmentColor0 = InfoReader.GetColor(colorScheme, "environmentColor0", location, report.SongWarning),
                        environmentColor1 = InfoReader.GetColor(colorScheme, "environmentColor1", location, report.SongWarning),
                        obstaclesColor = InfoReader.GetColor(colorScheme, "obstaclesColor", location, report.SongWarning),
                        environmentColor0Boost = InfoReader.GetColor(colorScheme, "environmentColor0Boost", location, report.SongWarning),
                        environmentColor1Boost = InfoReader.GetColor(colorScheme, "environmentColor1Boost", location, report.SongWarning),
                        environmentColorW = InfoReader.GetColor(colorScheme, "environmentColorW", location, report.SongWarning),
                        environmentColorWBoost = InfoReader.GetColor(colorScheme, "environmentColorWBoost", location, report.SongWarning)
                    };
                })
                .ToArray();
        }

        /// <summary>
        /// Makes sure that every array is set, also for data read from the cache, which may have been written
        /// by a version that left fields unset.
        /// </summary>
        [OnDeserialized]
        internal void OnDeserialized(StreamingContext context)
        {
            Normalize();
        }

        private void Normalize()
        {
            _genreTags = RemoveNulls(_genreTags);
            _environmentNames ??= Array.Empty<string>();
            _colorSchemes = (_colorSchemes ?? Array.Empty<ColorScheme>()).Select(c => c ?? new ColorScheme()).ToArray();
            _validationErrors = RemoveNulls(_validationErrors);
            _validationWarnings = RemoveNulls(_validationWarnings);
            _characteristicDetails = _characteristicDetails?.Where(c => c is { _beatmapCharacteristicName: not null }).ToArray();

            contributors = (contributors ?? Array.Empty<Contributor>()).Where(c => c != null).ToArray();
            foreach (var contributor in contributors)
            {
                contributor._role ??= string.Empty;
                contributor._name ??= string.Empty;
            }

            _difficulties = (_difficulties ?? Array.Empty<DifficultyData>()).Where(d => d is { _beatmapCharacteristicName: not null }).ToArray();
            foreach (var difficulty in _difficulties)
            {
                difficulty._difficultyLabel ??= string.Empty;
                difficulty._styleTags = RemoveNulls(difficulty._styleTags);
                difficulty._validationErrors = RemoveNulls(difficulty._validationErrors);
                difficulty._validationWarnings = RemoveNulls(difficulty._validationWarnings);

                var requirements = difficulty.additionalDifficultyData ??= new RequirementData();
                requirements._requirements = RemoveNulls(requirements._requirements);
                requirements._suggestions = RemoveNulls(requirements._suggestions);
                requirements._warnings = RemoveNulls(requirements._warnings);
                requirements._information = RemoveNulls(requirements._information);
            }
        }

        private static string[] RemoveNulls(string[]? values)
        {
            if (values == null)
            {
                return Array.Empty<string>();
            }

            return values.Any(v => v == null) ? values.Where(v => v != null).ToArray() : values;
        }

        public static MapColor? GetMapColorFromJObject(JObject jObject, string key)
        {
            if (jObject.TryGetValue(key, out var envColorWhiteBoost))
            {
                if (envColorWhiteBoost.Children().Count() >= 3)
                {
                    return new MapColor(
                        (float) (envColorWhiteBoost["r"] ?? 0),
                        (float) (envColorWhiteBoost["g"] ?? 0),
                        (float) (envColorWhiteBoost["b"] ?? 0),
                        (float) (envColorWhiteBoost["a"] ?? 1));
                }
            }
            return null;
        }

        /// <summary>
        /// Collects the problems found while reading an info file.
        /// </summary>
        private sealed class ValidationReport
        {
            internal readonly List<string> SongErrors = new List<string>();
            internal readonly List<string> SongWarnings = new List<string>();
            private readonly Dictionary<DifficultyData, List<string>> _extraDifficultyErrors = new Dictionary<DifficultyData, List<string>>();
            private readonly List<DifficultyData> _difficulties = new List<DifficultyData>();

            internal int ErrorCount => SongErrors.Count + _difficulties.Sum(d => d._validationErrors.Length);
            internal int WarningCount => SongWarnings.Count + _difficulties.Sum(d => d._validationWarnings.Length);

            internal void SongError(string message) => SongErrors.Add(message);
            internal void SongWarning(string message) => SongWarnings.Add(message);

            internal void AddDifficultyError(DifficultyData difficulty, string message)
            {
                if (!_extraDifficultyErrors.TryGetValue(difficulty, out var errors))
                {
                    errors = new List<string>();
                    _extraDifficultyErrors[difficulty] = errors;
                }

                if (!errors.Contains(message))
                {
                    errors.Add(message);
                }
            }

            internal void ApplyTo(DifficultyData[] difficulties)
            {
                _difficulties.Clear();
                _difficulties.AddRange(difficulties);

                foreach (var (difficulty, errors) in _extraDifficultyErrors)
                {
                    difficulty._validationErrors = difficulty._validationErrors.Concat(errors).ToArray();
                }
            }

            internal IEnumerable<string> AllErrors()
            {
                return SongErrors.Concat(_difficulties.SelectMany(d => d._validationErrors.Select(e => $"{d._beatmapCharacteristicName} {d._difficulty}: {e}")));
            }

            internal IEnumerable<string> AllWarnings()
            {
                return SongWarnings.Concat(_difficulties.SelectMany(d => d._validationWarnings.Select(w => $"{d._beatmapCharacteristicName} {d._difficulty}: {w}")));
            }
        }

        /// <summary>
        /// Reads values from an info file without throwing. A value of the wrong type is reported and treated
        /// as missing. Conversions the previous reader accepted, such as a number for a string, still work.
        /// </summary>
        private static class InfoReader
        {
            /// <summary>
            /// Messages stored on a difficulty are shown under that difficulty, so they need no location.
            /// </summary>
            internal static string Prefix(string location) => location.Length == 0 ? string.Empty : location + ": ";

            internal static JObject? GetObject(JObject parent, string key, string location, Action<string> report)
            {
                if (!parent.TryGetValue(key, out var token) || token.Type == JTokenType.Null)
                {
                    return null;
                }

                if (token is JObject jObject)
                {
                    return jObject;
                }

                report($"{InfoReader.Prefix(location)}'{key}' is not an object.");
                return null;
            }

            internal static IEnumerable<JToken> GetArray(JObject parent, string key, string location, Action<string> report, bool required = false)
            {
                if (!parent.TryGetValue(key, out var token) || token.Type == JTokenType.Null)
                {
                    if (required)
                    {
                        report($"{InfoReader.Prefix(location)}'{key}' is missing.");
                    }

                    return Array.Empty<JToken>();
                }

                if (token is JArray jArray)
                {
                    return jArray;
                }

                report($"{InfoReader.Prefix(location)}'{key}' is not an array.");
                return Array.Empty<JToken>();
            }

            internal static string[] GetStringArray(JObject parent, string key, string location, Action<string> report)
            {
                var values = new List<string>();
                foreach (var element in GetArray(parent, key, location, report))
                {
                    switch (element.Type)
                    {
                        case JTokenType.Null:
                            break;
                        case JTokenType.Object:
                        case JTokenType.Array:
                            report(location.Length == 0 ? $"An entry of '{key}' is not a text value." : $"{location}: an entry of '{key}' is not a text value.");
                            break;
                        default:
                            values.Add(element.ToString());
                            break;
                    }
                }

                return values.ToArray();
            }

            internal static string? GetString(JObject parent, string key, string location, Action<string> report)
            {
                if (!parent.TryGetValue(key, out var token) || token.Type == JTokenType.Null)
                {
                    return null;
                }

                if (token is JValue value)
                {
                    return value.ToString();
                }

                report($"{InfoReader.Prefix(location)}'{key}' is not a text value.");
                return null;
            }

            internal static T? GetValue<T>(JObject parent, string key, string location, Action<string> report) where T : struct
            {
                if (!parent.TryGetValue(key, out var token) || token.Type == JTokenType.Null)
                {
                    return null;
                }

                try
                {
                    return token.ToObject<T?>();
                }
                catch (Exception)
                {
                    report($"{InfoReader.Prefix(location)}'{key}' has an invalid value '{token}'.");
                    return null;
                }
            }

            internal static MapColor? GetColor(JObject parent, string key, string location, Action<string> report)
            {
                try
                {
                    return GetMapColorFromJObject(parent, key);
                }
                catch (Exception)
                {
                    report($"{InfoReader.Prefix(location)}'{key}' is not a valid color.");
                    return null;
                }
            }
        }
    }
}

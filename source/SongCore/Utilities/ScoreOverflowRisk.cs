using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using SongCore.Data;

namespace SongCore.Utilities
{
    /// <summary>
    /// Finds difficulties whose scores can go over 100% on leaderboards that count chain heads as 85 points.
    /// <para>
    /// The game gives a chain head that is also the head or tail of an arc the full 115 points (scoring types
    /// ChainHeadArcHead, ChainHeadArcTail and ChainHeadArcHeadArcTail), and part of it is fixed whatever the swing.
    /// A maximum score computed with 85 points for every chain head is lower than what can actually be scored.
    /// </para>
    /// <para>
    /// The game attaches arcs to notes by time and position only, regardless of color, and so does this check.
    /// Beatmaps in format 2 cannot contain chains.
    /// </para>
    /// </summary>
    internal static class ScoreOverflowRisk
    {
        // The game attaches arcs to notes whose times are within a millisecond, which it still does at exactly a
        // millisecond because of rounding, so a little is added. Beats are converted with the song's BPM, which is
        // exact unless the BPM changes in between.
        private const float TimeToleranceSeconds = 0.00105f;

        // Results by song data and difficulty. Song data is replaced whenever its folder changes, so results never
        // outlive the files they were computed from.
        private static readonly ConditionalWeakTable<SongData, ConcurrentDictionary<string, bool>> Results = new ConditionalWeakTable<SongData, ConcurrentDictionary<string, bool>>();

        /// <summary>
        /// Whether the difficulty has a chain head on an arc. The beatmap is read at most once per difficulty for as
        /// long as its song data stays the same. Returns false if it cannot be read.
        /// </summary>
        internal static bool HasChainHeadOnArc(SongData songData, IBeatmapLevelData beatmapLevelData, BeatmapKey beatmapKey, float beatsPerMinute)
        {
            var results = Results.GetOrCreateValue(songData);
            var difficultyKey = $"{beatmapKey.beatmapCharacteristic.serializedName}/{beatmapKey.difficulty}";
            if (results.TryGetValue(difficultyKey, out var result))
            {
                return result;
            }

            try
            {
                var json = beatmapLevelData.GetBeatmapString(in beatmapKey);
                result = json != null && HasChainHeadOnArc(json, beatsPerMinute);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"Could not check {beatmapLevelData.name} ({difficultyKey}) for scores over 100%: {ex.Message}");
                result = false;
            }

            results[difficultyKey] = result;
            return result;
        }

        internal static bool HasChainHeadOnArc(string json, float beatsPerMinute)
        {
            var beatTolerance = beatsPerMinute > 0f ? TimeToleranceSeconds * beatsPerMinute / 60f : TimeToleranceSeconds * 2f;

            var notes = new List<Position>();
            var chainHeads = new List<Position>();
            var arcEnds = new List<Position>();

            // Notes are kept as read until the format is known. Format 4 refers to the positions through indices
            // into colorNotesData, and writers may leave out an index of 0, so the index alone cannot tell.
            var rawNotes = new List<(float beat, int x, int y, int index)>();
            string? version = null;
            var noteRefs = new List<(float beat, int index)>();
            var chainRefs = new List<(float beat, int index)>();
            var arcRefs = new List<(float beat, int index)>();
            var noteData = new List<(int x, int y)>();

            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                {
                    return false;
                }

                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    var name = (string)reader.Value!;
                    if (!reader.Read())
                    {
                        break;
                    }

                    if (reader.TokenType == JsonToken.String && (name == "version" || name == "_version"))
                    {
                        version = (string)reader.Value!;
                        continue;
                    }

                    if (reader.TokenType != JsonToken.StartArray)
                    {
                        reader.Skip();
                        continue;
                    }

                    switch (name)
                    {
                        // Format 3 notes, and format 4 notes, which have an index instead of a position.
                        case "colorNotes":
                            ReadObjects(reader, values => rawNotes.Add((Get(values, "b"), (int)Get(values, "x"), (int)Get(values, "y"), (int)Get(values, "i"))));
                            break;
                        case "colorNotesData":
                            ReadObjects(reader, values => noteData.Add(((int)Get(values, "x"), (int)Get(values, "y"))));
                            break;
                        // Format 3 chains and arcs.
                        case "burstSliders":
                            ReadObjects(reader, values => chainHeads.Add(new Position(Get(values, "b"), (int)Get(values, "x"), (int)Get(values, "y"))));
                            break;
                        case "sliders":
                            ReadObjects(reader, values =>
                            {
                                arcEnds.Add(new Position(Get(values, "b"), (int)Get(values, "x"), (int)Get(values, "y")));
                                arcEnds.Add(new Position(Get(values, "tb"), (int)Get(values, "tx"), (int)Get(values, "ty")));
                            });
                            break;
                        // Format 4 chains and arcs.
                        case "chains":
                            ReadObjects(reader, values => chainRefs.Add((Get(values, "hb"), (int)Get(values, "i"))));
                            break;
                        case "arcs":
                            ReadObjects(reader, values =>
                            {
                                arcRefs.Add((Get(values, "hb"), (int)Get(values, "hi")));
                                arcRefs.Add((Get(values, "tb"), (int)Get(values, "ti")));
                            });
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
            }

            // Format 2 has no chains, so it never gets here with any.
            var isFormat4 = version != null && version.StartsWith("4", StringComparison.Ordinal);
            foreach (var (beat, x, y, index) in rawNotes)
            {
                if (isFormat4)
                {
                    noteRefs.Add((beat, index));
                }
                else
                {
                    notes.Add(new Position(beat, x, y));
                }
            }

            Resolve(noteRefs, noteData, notes);
            Resolve(chainRefs, noteData, chainHeads);
            Resolve(arcRefs, noteData, arcEnds);

            if (chainHeads.Count == 0 || arcEnds.Count == 0)
            {
                return false;
            }

            // A chain head only scores as one if there is a note for it.
            foreach (var chainHead in chainHeads)
            {
                if (Contains(arcEnds, chainHead, beatTolerance) && Contains(notes, chainHead, beatTolerance))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Resolve(List<(float beat, int index)> refs, List<(int x, int y)> data, List<Position> positions)
        {
            foreach (var (beat, index) in refs)
            {
                if (index >= 0 && index < data.Count)
                {
                    positions.Add(new Position(beat, data[index].x, data[index].y));
                }
            }
        }

        private static bool Contains(List<Position> positions, Position position, float beatTolerance)
        {
            foreach (var candidate in positions)
            {
                if (candidate.X == position.X && candidate.Y == position.Y && Math.Abs(candidate.Beat - position.Beat) <= beatTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads an array of objects, collecting their numeric properties. Anything else is skipped.
        /// </summary>
        private static void ReadObjects(JsonTextReader reader, Action<Dictionary<string, double>> handle)
        {
            var values = new Dictionary<string, double>();
            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
            {
                if (reader.TokenType != JsonToken.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                values.Clear();
                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    var name = (string)reader.Value!;
                    if (!reader.Read())
                    {
                        return;
                    }

                    if (reader.TokenType is JsonToken.Integer or JsonToken.Float)
                    {
                        values[name] = Convert.ToDouble(reader.Value);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                handle(values);
            }
        }

        // Properties left out of the files take their default value.
        private static float Get(Dictionary<string, double> values, string name)
        {
            return values.TryGetValue(name, out var value) ? (float)value : 0f;
        }

        private readonly struct Position
        {
            public readonly float Beat;
            public readonly int X;
            public readonly int Y;

            public Position(float beat, int x, int y)
            {
                Beat = beat;
                X = x;
                Y = y;
            }
        }
    }
}

using System;
using Newtonsoft.Json;

namespace SongCore.Data
{
    [Serializable]
    public class SongHashData
    {
        public long directoryHash;
        public string songHash;

        [JsonConstructor]
        public SongHashData(long directoryHash, string songHash)
        {
            this.directoryHash = directoryHash;
            this.songHash = songHash;
        }
    }

    [Serializable]
    public class AudioCacheData
    {
        // Durations used to be validated against the level ID, which changes whenever a duplicate
        // beatmap wins or loses the unsuffixed level ID, causing needless re-parsing of the audio file.
        // The directory hash describes the beatmap folder itself, so it does not have that problem.
        public long directoryHash;
        public float duration;

        /// <summary>
        /// Level ID of the beatmap the duration belongs to, as in 3.16.0, for mods that read it. It is set again
        /// every time the duration is looked up, since level IDs are not stable, and is not saved.
        /// </summary>
        [JsonIgnore]
        public string? id;

        [JsonConstructor]
        public AudioCacheData(long directoryHash, float duration)
        {
            this.directoryHash = directoryHash;
            this.duration = duration;
        }

        /// <summary>
        /// The constructor of 3.16.0, kept for mods built against it.
        /// </summary>
        public AudioCacheData(string audioFileHash, float duration)
        {
            id = audioFileHash;
            this.duration = duration;
        }
    }
}
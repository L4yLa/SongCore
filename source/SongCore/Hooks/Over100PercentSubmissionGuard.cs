using SiraUtil.Submissions;
using Zenject;

namespace SongCore.Hooks
{
    /// <summary>
    /// Safety net for <see cref="PluginConfig.BlockOver100PercentDifficulties"/>. The menu only blocks the play button,
    /// so difficulties started any other way, such as in multiplayer or by other mods, are caught here, and score
    /// submission is disabled the same way as for autoplay.
    /// <para>
    /// This checks the beatmap data the game is about to play rather than the beatmap file, so it is exactly what the
    /// game scores: a chain head that is also part of an arc scores out of 115 instead of 85, which leaderboards such as
    /// ScoreSaber may see as a score over 100%. Practice is left alone, as it does not submit scores.
    /// </para>
    /// </summary>
    internal class Over100PercentSubmissionGuard : IInitializable
    {
        private readonly PluginConfig _config;
        private readonly Submission _submission;
        private readonly IReadonlyBeatmapData? _beatmapData;
        private readonly GameplayCoreSceneSetupData? _sceneSetupData;

        private Over100PercentSubmissionGuard(PluginConfig config, Submission submission, [InjectOptional] IReadonlyBeatmapData? beatmapData, [InjectOptional] GameplayCoreSceneSetupData? sceneSetupData)
        {
            _config = config;
            _submission = submission;
            _beatmapData = beatmapData;
            _sceneSetupData = sceneSetupData;
        }

        public void Initialize()
        {
            if (!_config.BlockOver100PercentDifficulties || _beatmapData == null || _sceneSetupData?.practiceSettings != null)
            {
                return;
            }

            foreach (var noteData in _beatmapData.GetBeatmapDataItems<NoteData>(0))
            {
                if (noteData.scoringType is NoteData.ScoringType.ChainHeadArcHead or NoteData.ScoringType.ChainHeadArcTail or NoteData.ScoringType.ChainHeadArcHeadArcTail)
                {
                    Plugin.Log.Info($"Disabling score submission, as a chain head at {noteData.time:0.###}s is also part of an arc and may score over 100% on some leaderboards.");
                    _submission.DisableScoreSubmission(nameof(SongCore), "This difficulty may score over 100% on ScoreSaber.");
                    return;
                }
            }
        }
    }
}

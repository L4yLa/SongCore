using System;
using System.Collections.Generic;
using IPA.Utilities;

namespace SongCore.Utilities
{
    internal static class Accessors
    {
        public static readonly FieldAccessor<BeatmapLevel, string>.Accessor LevelIDAccessor =
            FieldAccessor<BeatmapLevel, string>.GetAccessor(nameof(BeatmapLevel.levelID));

        public static readonly FieldAccessor<BeatmapLevel, float>.Accessor SongDurationAccessor =
            FieldAccessor<BeatmapLevel, float>.GetAccessor(nameof(BeatmapLevel.songDuration));

        public static readonly FieldAccessor<BeatmapLevel, float>.Accessor PreviewDurationAccessor =
            FieldAccessor<BeatmapLevel, float>.GetAccessor(nameof(BeatmapLevel.previewDuration));

        public static readonly FieldAccessor<BeatmapLevelPack, List<BeatmapLevel>>.Accessor AllBeatmapLevelsAccessor =
            FieldAccessor<BeatmapLevelPack, List<BeatmapLevel>>.GetAccessor(nameof(BeatmapLevelPack._allBeatmapLevels));

        public static readonly FieldAccessor<SaberManager, SaberManager.InitData>.Accessor SaberManagerInitDataAccessor =
            FieldAccessor<SaberManager, SaberManager.InitData>.GetAccessor(nameof(SaberManager._initData));

        public static readonly FieldAccessor<BeatmapLevelsRepository, BeatmapLevelPack[]>.Accessor BeatmapLevelPacksAccessor =
            FieldAccessor<BeatmapLevelsRepository, BeatmapLevelPack[]>.GetAccessor(nameof(BeatmapLevelsRepository._beatmapLevelPacks));

        public static readonly FieldAccessor<LevelCollectionTableView, Action<LevelCollectionTableView, BeatmapLevel>>.Accessor TableViewDidSelectLevelEventAccessor =
            FieldAccessor<LevelCollectionTableView, Action<LevelCollectionTableView, BeatmapLevel>>.GetAccessor(nameof(LevelCollectionTableView.didSelectLevelEvent));

        public static readonly FieldAccessor<LevelCollectionViewController, Action<LevelCollectionViewController, BeatmapLevel>>.Accessor ViewControllerDidSelectLevelEventAccessor =
            FieldAccessor<LevelCollectionViewController, Action<LevelCollectionViewController, BeatmapLevel>>.GetAccessor(nameof(LevelCollectionViewController.didSelectLevelEvent));

        public static readonly FieldAccessor<StandardLevelDetailViewController, Action<StandardLevelDetailViewController, StandardLevelDetailViewController.ContentType>>.Accessor DidChangeContentEventAccessor =
            FieldAccessor<StandardLevelDetailViewController, Action<StandardLevelDetailViewController, StandardLevelDetailViewController.ContentType>>.GetAccessor(nameof(StandardLevelDetailViewController.didChangeContentEvent));
    }
}

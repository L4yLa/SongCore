using SongCore.Hooks;
using SongCore.Hooks.BeatmapLevelCache;
using SongCore.UI;
using Zenject;

namespace SongCore.Installers
{
    internal class MenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<SettingsController>().AsSingle();
            Container.BindInterfacesAndSelfTo<Loader>().AsSingle();
            Container.BindInterfacesAndSelfTo<ColorsUI>().AsSingle();
            Container.Bind<ProgressBar>().FromNewComponentOnNewGameObject().AsSingle();
            Container.BindInterfacesAndSelfTo<RequirementsUI>().AsSingle();
            // The beatmap level cache of 3.16.0, kept for mods built against it. It only records what the game does.
            Container.BindInterfacesAndSelfTo<EventProxyHooks>().AsSingle();
            Container.Bind<BeatmapLevelCache>().AsSingle();
            Container.BindInterfacesTo<BeatmapJsonCacheHooks>().AsSingle();
            Container.BindInterfacesTo<BeatmapDataCacheHooks>().AsSingle();
            Container.BindInterfacesTo<IOBlacklistHooks>().AsSingle();
            Container.BindInterfacesTo<BeatmapVersionDetectionHook>().AsSingle();
            Container.BindInterfacesTo<SongDataMenuHooks>().AsSingle();
            Container.BindInterfacesTo<InternalRestartHook>().AsSingle();
            Container.BindInterfacesTo<BindBeatmapLevelHook>().AsSingle();
            Container.BindInterfacesTo<YeetLegacyBpmEventsHook>().AsSingle();
            Container.BindInterfacesTo<StandardLevelDetailViewControllerHook>().AsSingle();
            Container.BindInterfacesTo<RemoveDownloadIconHook>().AsSingle();
            Container.BindInterfacesTo<RefreshAfterEditorHook>().AsSingle();
            Container.BindInterfacesTo<LoadingHooks>().AsSingle();
            Container.BindInterfacesTo<LevelListTableCellDataHook>().AsSingle();
            Container.BindInterfacesTo<EscapeURLHook>().AsSingle();
            Container.BindInterfacesTo<ComputeMaxMultipliedScoreSafelyHook>().AsSingle();
        }
    }
}

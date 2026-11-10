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

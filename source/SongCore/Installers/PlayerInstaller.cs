using SongCore.Hooks;
using Zenject;

namespace SongCore.Installers
{
    /// <summary>
    /// Installed for every kind of player, including multiplayer, unlike <see cref="GameInstaller"/>.
    /// </summary>
    internal class PlayerInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.BindInterfacesTo<Over100PercentSubmissionGuard>().AsSingle();
        }
    }
}

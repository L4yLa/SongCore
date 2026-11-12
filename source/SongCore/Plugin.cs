using System.IO;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using IPA.Loader;
using IPA.Logging;
using IPA.Utilities;
using SiraUtil.Zenject;
using SongCore.Installers;
using SongCore.UI;

namespace SongCore
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        private readonly PluginMetadata _metadata;

        internal static Logger Log { get; private set; } = null!;

        [Init]
        public Plugin(Logger logger, PluginMetadata metadata, Zenjector zenjector)
        {
            // Workaround for creating BSIPA config in Userdata subdir
            Directory.CreateDirectory(Path.Combine(UnityGame.UserDataPath, nameof(SongCore)));

            Log = logger;
            _metadata = metadata;

            zenjector.UseLogger(logger);
            zenjector.Install<AppInstaller>(Location.App, Config.GetConfigFor(nameof(SongCore) + Path.DirectorySeparatorChar + nameof(SongCore)).Generated<PluginConfig>());
            zenjector.Install<MenuInstaller>(Location.Menu);
            zenjector.Install<GameInstaller>(Location.StandardPlayer);
            zenjector.Install<PlayerInstaller>(Location.Player);
        }

        [OnStart]
        public void OnApplicationStart()
        {
            BasicUI.GetIcons();

            // Song data used to be loaded here, on a fire and forget task. It is now loaded together with the
            // other caches in the Loader, because loading it also merges the journal, and letting that land
            // after beatmaps have started loading would throw away everything loaded so far.

            Collections.RegisterCustomCharacteristic(BasicUI.MissingCharIcon!, "Missing Characteristic", "Missing Characteristic", "MissingCharacteristic", "MissingCharacteristic", false, false, 1000);
            Collections.RegisterCustomCharacteristic(BasicUI.LightshowIcon!, "Lightshow", "Lightshow", "Lightshow", "Lightshow", false, false, 100);
            Collections.RegisterCustomCharacteristic(BasicUI.ExtraDiffsIcon!, "Lawless", "Lawless - Anything Goes", "Lawless", "Lawless", false, false, 101);

            var foldersXmlFilePath = Path.Combine(UnityGame.UserDataPath, nameof(SongCore), "folders.xml");
            if (!File.Exists(foldersXmlFilePath))
            {
                using var foldersXmlResourceStream = _metadata.Assembly.GetManifestResourceStream("SongCore.Data.folders.xml");
                using var fileStream = File.OpenWrite(foldersXmlFilePath);
                foldersXmlResourceStream!.CopyTo(fileStream);
            }

            Loader.SeparateSongFolders.InsertRange(0, Data.SeparateSongFolder.ReadSeparateFoldersFromFile(foldersXmlFilePath));
        }

        [OnExit]
        public void OnApplicationExit()
        {
            // Suppress BSIPA warning about missing [OnExit] annotated method
        }
    }
}

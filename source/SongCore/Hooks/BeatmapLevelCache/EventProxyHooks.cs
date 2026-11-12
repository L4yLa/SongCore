using System;
using MonoMod.RuntimeDetour;
using SongCore.Utilities;
using Zenject;

namespace SongCore.Hooks.BeatmapLevelCache
{
    /// <summary>
    /// Exposes the handlers of the level selection and content events, for mods built against 3.16.0. In 3.16.0 these
    /// handlers were taken out of the events and invoked by SongCore later on. Here they stay in the events, and the
    /// delegates are copied from the events whenever a handler is added or removed, so they always list the same
    /// handlers the game invokes.
    /// </summary>
    internal class EventProxyHooks : IInitializable, IDisposable
    {
        private Hook _levelCollectionTableViewAddHook = null!;
        private Hook _levelCollectionTableViewRemoveHook = null!;
        private Hook _levelCollectionViewControllerAddHook = null!;
        private Hook _levelCollectionViewControllerRemoveHook = null!;
        private Hook _standardLevelDetailViewControllerAddHook = null!;
        private Hook _standardLevelDetailViewControllerRemoveHook = null!;
        private Delegate? _levelCollectionTableViewDidSelectLevelDelegate;
        private Delegate? _levelCollectionViewControllerDidSelectLevelDelegate;
        private Delegate? _standardLevelDetailViewControllerDidChangeContentDelegate;

        public Delegate? LevelCollectionTableViewDidSelectLevelDelegate => _levelCollectionTableViewDidSelectLevelDelegate;
        public Delegate? LevelCollectionViewControllerDidSelectLevelDelegate => _levelCollectionViewControllerDidSelectLevelDelegate;
        public Delegate? StandardLevelDetailViewControllerDidChangeContentDelegate => _standardLevelDetailViewControllerDidChangeContentDelegate;

        public void Initialize()
        {
            _levelCollectionTableViewAddHook = new Hook(typeof(LevelCollectionTableView).GetMethod($"add_{nameof(LevelCollectionTableView.didSelectLevelEvent)}")!, AddLevelCollectionTableViewDidSelectLevelEvent, true);
            _levelCollectionTableViewRemoveHook = new Hook(typeof(LevelCollectionTableView).GetMethod($"remove_{nameof(LevelCollectionTableView.didSelectLevelEvent)}")!, RemoveLevelCollectionTableViewDidSelectLevelEvent, true);
            _levelCollectionViewControllerAddHook = new Hook(typeof(LevelCollectionViewController).GetMethod($"add_{nameof(LevelCollectionViewController.didSelectLevelEvent)}")!, AddLevelCollectionViewControllerDidSelectLevelEvent, true);
            _levelCollectionViewControllerRemoveHook = new Hook(typeof(LevelCollectionViewController).GetMethod($"remove_{nameof(LevelCollectionViewController.didSelectLevelEvent)}")!, RemoveLevelCollectionViewControllerDidSelectLevelEvent, true);
            _standardLevelDetailViewControllerAddHook = new Hook(typeof(StandardLevelDetailViewController).GetMethod($"add_{nameof(StandardLevelDetailViewController.didChangeContentEvent)}")!, AddDidChangeContentEvent, true);
            _standardLevelDetailViewControllerRemoveHook = new Hook(typeof(StandardLevelDetailViewController).GetMethod($"remove_{nameof(StandardLevelDetailViewController.didChangeContentEvent)}")!, RemoveDidChangeContentEvent, true);
        }

        public void Dispose()
        {
            _levelCollectionTableViewAddHook.Dispose();
            _levelCollectionTableViewRemoveHook.Dispose();
            _levelCollectionViewControllerAddHook.Dispose();
            _levelCollectionViewControllerRemoveHook.Dispose();
            _standardLevelDetailViewControllerAddHook.Dispose();
            _standardLevelDetailViewControllerRemoveHook.Dispose();
        }

        private void AddLevelCollectionTableViewDidSelectLevelEvent(Action<LevelCollectionTableView, Action<LevelCollectionTableView, BeatmapLevel>> original, LevelCollectionTableView instance, Action<LevelCollectionTableView, BeatmapLevel> value)
        {
            original(instance, value);
            _levelCollectionTableViewDidSelectLevelDelegate = Accessors.TableViewDidSelectLevelEventAccessor(ref instance);
        }

        private void RemoveLevelCollectionTableViewDidSelectLevelEvent(Action<LevelCollectionTableView, Action<LevelCollectionTableView, BeatmapLevel>> original, LevelCollectionTableView instance, Action<LevelCollectionTableView, BeatmapLevel> value)
        {
            original(instance, value);
            _levelCollectionTableViewDidSelectLevelDelegate = Accessors.TableViewDidSelectLevelEventAccessor(ref instance);
        }

        private void AddLevelCollectionViewControllerDidSelectLevelEvent(Action<LevelCollectionViewController, Action<LevelCollectionViewController, BeatmapLevel>> original, LevelCollectionViewController instance, Action<LevelCollectionViewController, BeatmapLevel> value)
        {
            original(instance, value);
            _levelCollectionViewControllerDidSelectLevelDelegate = Accessors.ViewControllerDidSelectLevelEventAccessor(ref instance);
        }

        private void RemoveLevelCollectionViewControllerDidSelectLevelEvent(Action<LevelCollectionViewController, Action<LevelCollectionViewController, BeatmapLevel>> original, LevelCollectionViewController instance, Action<LevelCollectionViewController, BeatmapLevel> value)
        {
            original(instance, value);
            _levelCollectionViewControllerDidSelectLevelDelegate = Accessors.ViewControllerDidSelectLevelEventAccessor(ref instance);
        }

        private void AddDidChangeContentEvent(Action<StandardLevelDetailViewController, Action<StandardLevelDetailViewController, StandardLevelDetailViewController.ContentType>> original, StandardLevelDetailViewController instance, Action<StandardLevelDetailViewController, StandardLevelDetailViewController.ContentType> value)
        {
            original(instance, value);
            _standardLevelDetailViewControllerDidChangeContentDelegate = Accessors.DidChangeContentEventAccessor(ref instance);
        }

        private void RemoveDidChangeContentEvent(Action<StandardLevelDetailViewController, Action<StandardLevelDetailViewController, StandardLevelDetailViewController.ContentType>> original, StandardLevelDetailViewController instance, Action<StandardLevelDetailViewController, StandardLevelDetailViewController.ContentType> value)
        {
            original(instance, value);
            _standardLevelDetailViewControllerDidChangeContentDelegate = Accessors.DidChangeContentEventAccessor(ref instance);
        }
    }
}

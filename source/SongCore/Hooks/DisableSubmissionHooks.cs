using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SiraUtil.Submissions;
using Zenject;

namespace SongCore.Hooks
{
    internal class DisableSubmissionHooks : IInitializable, IDisposable
    {
        // The hooks are created on the first level and kept for the rest of the session instead of being
        // created and removed every time a level starts. Only the submission of the current level changes,
        // so it is handed over through _current.
        private static Hook? _autoplayHook;
        private static Hook? _playbackHook;
        private static DisableSubmissionHooks? _current;

        private readonly Submission _submission;

        private Ticket? _ticket;

        private DisableSubmissionHooks(Submission submission)
        {
            _submission = submission;
        }

        public void Initialize()
        {
            _current = this;
            _autoplayHook ??= new Hook(typeof(RecPlayBehaviour).GetMethod(nameof(RecPlayBehaviour.Play), BindingFlags.Instance | BindingFlags.NonPublic)!, AutoplayCheck, true);
            _playbackHook ??= new Hook(typeof(ObjectsMovementRecorder).GetMethod(nameof(ObjectsMovementRecorder.Init), BindingFlags.Instance | BindingFlags.Public)!, PlaybackCheck, true);
        }

        public void Dispose()
        {
            if (_current == this)
            {
                _current = null;
            }
        }

        private static void AutoplayCheck(Action<RecPlayBehaviour> original, RecPlayBehaviour instance)
        {
            original(instance);
            _current?.DisableSubmission("Autoplay is enabled.");
        }

        private static void PlaybackCheck(Action<ObjectsMovementRecorder> original, ObjectsMovementRecorder instance)
        {
            original(instance);
            if (instance._mode == ObjectsMovementRecorder.Mode.Playback)
            {
                _current?.DisableSubmission("Playback is enabled.");
            }
        }

        private void DisableSubmission(string reason)
        {
            _ticket ??= _submission.DisableScoreSubmission(nameof(SongCore), reason);
        }
    }
}

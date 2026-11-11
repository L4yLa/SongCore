using System;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;
using Zenject;

namespace SongCore.Hooks
{
    /// <summary>
    /// By default, the provider uses the highest note jump speed value, capping it at <see cref="VariableMovementDataProvider.kMinNoteJumpMovementSpeed"/>.
    /// This allows it to also use the lowest NJS value when the initial one is negative, capping it at -<see cref="VariableMovementDataProvider.kMinNoteJumpMovementSpeed"/>.
    /// </summary>
    internal class AllowNegativeVariableNoteJumpSpeedHook : IInitializable, IDisposable
    {
        // Installed once for the whole application rather than every time a level starts. The provider being
        // updated is taken from the patched method itself, so nothing depends on the game scene container.
        private ILHook _variableMovementDataProviderHook = null!;

        public void Initialize()
        {
            _variableMovementDataProviderHook = new ILHook(typeof(VariableMovementDataProvider).GetMethod(nameof(VariableMovementDataProvider.ManualUpdate), BindingFlags.Instance | BindingFlags.NonPublic)!, ctx =>
            {
                var cursor = new ILCursor(ctx);
                cursor.RemoveRange(10);
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldarg_0);
                cursor.Emit(OpCodes.Ldarg_1);
                cursor.EmitDelegate<Func<VariableMovementDataProvider, float, float>>(GetNoteJumpMovementSpeed);
            }, true);
        }

        public void Dispose()
        {
            _variableMovementDataProviderHook.Dispose();
        }

        private static float GetNoteJumpMovementSpeed(VariableMovementDataProvider variableMovementDataProvider, float songTime)
        {
            var noteJumpSpeed = variableMovementDataProvider._initNoteJumpMovementSpeed + variableMovementDataProvider._relativeNoteJumpSpeedInterpolation.GetValue(songTime);
            return variableMovementDataProvider._initNoteJumpMovementSpeed > 0
                ? Mathf.Max(noteJumpSpeed, VariableMovementDataProvider.kMinNoteJumpMovementSpeed)
                : Mathf.Min(noteJumpSpeed, -VariableMovementDataProvider.kMinNoteJumpMovementSpeed);
        }
    }
}

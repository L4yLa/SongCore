using System.Reflection;
using MonoMod.Utils;

namespace SongCore.Utilities
{
    internal static class MethodBaseExtensions
    {
        // TODO: Remove this once MonoMod.RuntimeDetour publicizes its own version of this method.
        public static MethodInfo CreateILCopy(this MethodBase method)
        {
            using var dmd = new DynamicMethodDefinition(method);
            return dmd.Generate();
        }
    }
}

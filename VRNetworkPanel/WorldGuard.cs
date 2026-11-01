using System;
using Unity.Entities;

namespace VRNetworkPanel
{
    /// <summary>
    /// "Is the client world safe to call into right now."
    ///
    /// Leave Game (or the Disconnect console command) tears the client world down, and calling
    /// into a system of a freed world is a NATIVE access violation, not a managed exception: no
    /// try/catch sees it, nothing reaches the log, and the game simply closes. So every probe
    /// asks this first, and nothing taken from the world is kept across probes.
    ///
    /// FAILS CLOSED: a wrong "no" costs one probe showing dashes; a wrong "yes" is the crash.
    /// </summary>
    internal static class WorldGuard
    {
        internal static bool Usable(World w)
        {
            if (w == null) return false;
            try
            {
                IntPtr ptr = w.Pointer;
                if (ptr == IntPtr.Zero || !w.IsCreated) return false;

                // Belt and braces: a disposed world is removed from the static registry.
                // Il2CppInterop hands back a NEW wrapper per element, so compare pointers.
                var all = World.s_AllWorlds;
                // Cannot prove membership, so "no": fail closed. A cached world reaches this line
                // without a fresh list walk, and IsCreated alone is not proof enough to call in.
                if (all == null) return false;
                for (int i = 0; i < all.Count; i++)
                {
                    var o = all[i];
                    if (o != null && o.Pointer == ptr) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}

using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade.ViewModelCollection.Multiplayer;

namespace CalradiaTavern.Patches
{
    [HarmonyPatch(typeof(MPChatVM), nameof(MPChatVM.IsChatAllowedByOptions))]
    internal static class MPChatVMOptionsPatch
    {
        private static bool Prefix(ref bool __result)
        {
            if (Game.Current?.GameType is Campaign)
            {
                __result = true;
                return false;
            }

            return true;
        }
    }
}

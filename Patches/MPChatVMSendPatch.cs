using System;
using System.Linq;
using System.Reflection;
using CalradiaTavern.Behaviors;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.ViewModelCollection.Multiplayer;

namespace CalradiaTavern.Patches
{
    [HarmonyPatch(typeof(MPChatVM), nameof(MPChatVM.SendMessageToChannel))]
    internal static class MPChatVMSendPatch
    {
        private static readonly MethodInfo AddMessageMethod = typeof(MPChatVM)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == "AddMessage" && x.GetParameters().Length == 4);

        private static bool Prefix(MPChatVM __instance, object __0, string message)
        {
            if (!(Game.Current?.GameType is Campaign))
            {
                return true;
            }

            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                return true;
            }

            string text = (message ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                __instance.WrittenText = string.Empty;
                return false;
            }

            if (text.Length > 180)
            {
                text = text.Substring(0, 180);
            }

            try
            {
                string result = behavior.SendChat(text);
                if (result.StartsWith("Send failed:", StringComparison.OrdinalIgnoreCase))
                {
                    InformationManager.DisplayMessage(
                        new InformationMessage("[Chat] " + result, Colors.Red)
                    );
                }
                else
                {
                    string sender = string.IsNullOrWhiteSpace(behavior.DisplayName)
                        ? "Me"
                        : behavior.DisplayName.Trim();

                    AddMessageMethod?.Invoke(__instance, new[] { text, sender, __0, null });

                    InformationManager.DisplayMessage(
                        new InformationMessage("[Chat] " + sender + ": " + text, Colors.Cyan)
                    );
                }
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("MPChatVMSendPatch.Prefix", ex);
            }

            __instance.WrittenText = string.Empty;
            return false;
        }
    }
}

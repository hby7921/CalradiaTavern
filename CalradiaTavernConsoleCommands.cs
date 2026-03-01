using System.Collections.Generic;
using CalradiaTavern.Behaviors;
using TaleWorlds.Library;

namespace CalradiaTavern
{
    public static class CalradiaTavernConsoleCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("chat_send", "ctavern")]
        public static string ChatSend(List<string> args)
        {
            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                return "Calradia Tavern behavior is not available in this save.";
            }

            string text = string.Join(" ", args ?? new List<string>()).Trim();
            if (text.Length == 0)
            {
                return "Usage: ctavern.chat_send <message>";
            }

            return behavior.SendChat(text);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("chat_pull", "ctavern")]
        public static string ChatPull(List<string> args)
        {
            _ = args;
            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            return behavior == null ? "Calradia Tavern behavior is not available in this save." : behavior.PullNow();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("send_item", "ctavern")]
        public static string SendItem(List<string> args)
        {
            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                return "Calradia Tavern behavior is not available in this save.";
            }

            if (args == null || args.Count < 3)
            {
                return "Usage: ctavern.send_item <player_name> <item_id> <count>";
            }

            if (!int.TryParse(args[2], out int count))
            {
                return "Count must be a number.";
            }

            return behavior.SendItemToPlayer(args[0], args[1], count);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("set_name", "ctavern")]
        public static string SetName(List<string> args)
        {
            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                return "Calradia Tavern behavior is not available in this save.";
            }

            string value = string.Join(" ", args ?? new List<string>()).Trim();
            if (value.Length == 0)
            {
                return "Usage: ctavern.set_name <display_name>";
            }

            return behavior.SetDisplayName(value);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("set_server", "ctavern")]
        public static string SetServer(List<string> args)
        {
            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                return "Calradia Tavern behavior is not available in this save.";
            }

            string url = string.Join(" ", args ?? new List<string>()).Trim();
            if (url.Length == 0)
            {
                return "Usage: ctavern.set_server <http://ip:port>";
            }

            return behavior.SetServerUrl(url);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("open", "ctavern")]
        public static string OpenUi(List<string> args)
        {
            _ = args;
            CalradiaTavern.UI.CalradiaTavernScreenManager.Open();
            return "Calradia Tavern UI opened.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("help", "ctavern")]
        public static string Help(List<string> args)
        {
            _ = args;
            return string.Join(
                "\n",
                new[]
                {
                    "ctavern.chat_send <message>",
                    "ctavern.chat_pull",
                    "ctavern.send_item <player_name> <item_id> <count>",
                    "ctavern.set_name <display_name>",
                    "ctavern.set_server <http://ip:port>",
                    "ctavern.open",
                }
            );
        }
    }
}

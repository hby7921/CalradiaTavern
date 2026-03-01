using TaleWorlds.ScreenSystem;

namespace CalradiaTavern.UI
{
    internal static class CalradiaTavernScreenManager
    {
        public static bool IsOpen => ScreenManager.TopScreen is CalradiaTavernScreen;

        public static void Open()
        {
            if (IsOpen)
            {
                CalradiaTavernDebug.Trace("ScreenManager", "Open skipped: already open");
                return;
            }

            try
            {
                CalradiaTavernDebug.Trace("ScreenManager", "PushScreen CalradiaTavernScreen");
                ScreenManager.PushScreen(new CalradiaTavernScreen());
            }
            catch (System.Exception ex)
            {
                CalradiaTavernDebug.ReportException("ScreenManager.Open", ex);
            }
        }

        public static void Close()
        {
            if (ScreenManager.TopScreen is CalradiaTavernScreen)
            {
                try
                {
                    CalradiaTavernDebug.Trace("ScreenManager", "PopScreen CalradiaTavernScreen");
                    ScreenManager.PopScreen();
                }
                catch (System.Exception ex)
                {
                    CalradiaTavernDebug.ReportException("ScreenManager.Close", ex);
                }
            }
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
                return;
            }

            Open();
        }
    }
}

using System;
using System.IO;
using System.Text;
using TaleWorlds.Library;

namespace CalradiaTavern
{
    internal static class CalradiaTavernDebug
    {
        private static readonly object Sync = new object();
        private static string _logPath;
        private static string _desktopLogPath;
        private static bool _initialized;

        public static string BuildTag => "DBG-2026-03-01-01:00";

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _logPath = ResolveLogPath();
            _desktopLogPath = ResolveDesktopLogPath();
            _initialized = true;
            Trace("Debug", "Initialized. LogPath=" + _logPath + " DesktopLogPath=" + _desktopLogPath);
        }

        public static void Trace(string source, string message)
        {
            if (!_initialized)
            {
                Initialize();
            }

            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [" + source + "] " + message;
            try
            {
                lock (Sync)
                {
                    AppendLineSafe(_logPath, line);
                    if (!string.IsNullOrEmpty(_desktopLogPath) &&
                        !string.Equals(_desktopLogPath, _logPath, StringComparison.OrdinalIgnoreCase))
                    {
                        AppendLineSafe(_desktopLogPath, line);
                    }
                }
            }
            catch
            {
                // Ignore any logging failure to avoid affecting gameplay.
            }
        }

        public static void ReportException(string source, Exception ex)
        {
            string reason = ex == null
                ? "Unknown exception"
                : (ex.GetType().Name + ": " + ex.Message);

            Trace(source, "EXCEPTION " + reason);
            if (ex != null)
            {
                Trace(source, ex.ToString());
            }

            ShowInGame(source + " -> " + reason);
        }

        public static void ShowInGame(string text)
        {
            try
            {
                string show = text ?? string.Empty;
                if (show.Length > 180)
                {
                    show = show.Substring(0, 180) + "...";
                }

                InformationManager.DisplayMessage(new InformationMessage("[CalradiaTavern] " + show));
            }
            catch
            {
                // Ignore UI message errors.
            }
        }

        private static string ResolveLogPath()
        {
            try
            {
                string moduleDir = Path.GetFullPath(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Modules", "CalradiaTavern")
                );

                if (Directory.Exists(moduleDir))
                {
                    return Path.Combine(moduleDir, "CalradiaTavern.debug.log");
                }
            }
            catch
            {
            }

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                {
                    return Path.Combine(desktop, "CalradiaTavern.debug.log");
                }
            }
            catch
            {
            }

            return Path.Combine(Path.GetTempPath(), "CalradiaTavern.debug.log");
        }

        private static string ResolveDesktopLogPath()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                {
                    return Path.Combine(desktop, "CalradiaTavern.debug.log");
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static void AppendLineSafe(string path, string line)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Ignore append failures.
            }
        }
    }
}

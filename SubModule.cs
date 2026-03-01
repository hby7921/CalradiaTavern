using System;
using CalradiaTavern.Behaviors;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;

namespace CalradiaTavern
{
    public class SubModule : MBSubModuleBase
    {
        private const string BuildMarker = "CTavern build 2026-03-01 22:35 ui-stable-shell";
        private const float BackgroundPollIntervalSeconds = 1.5f;

        private float _backgroundPollElapsed;
        private bool _chatInputOpen;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            CalradiaTavernDebug.Initialize();
            CalradiaTavernDebug.Trace(
                "SubModule",
                "OnSubModuleLoad " + BuildMarker + " " + CalradiaTavernDebug.BuildTag
            );
        }

        protected override void OnSubModuleUnloaded()
        {
            CalradiaTavernDebug.Trace("SubModule", "OnSubModuleUnloaded");
            base.OnSubModuleUnloaded();
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            CalradiaTavernDebug.Trace("SubModule", "OnGameStart");

            if (!(game.GameType is Campaign))
            {
                return;
            }

            if (gameStarterObject is CampaignGameStarter starter)
            {
                starter.AddBehavior(new CalradiaTavernCampaignBehavior());
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            if (!(Game.Current?.GameType is Campaign))
            {
                return;
            }

            if (!IsMapScreenActive())
            {
                return;
            }

            _backgroundPollElapsed += Math.Max(0f, dt);
            if (_backgroundPollElapsed >= BackgroundPollIntervalSeconds)
            {
                _backgroundPollElapsed = 0f;
                try
                {
                    CalradiaTavernCampaignBehavior.Instance?.PullNow();
                }
                catch (Exception ex)
                {
                    CalradiaTavernDebug.ReportException("SubModule.OnApplicationTick.PullNow", ex);
                }
            }

            if (!Input.IsKeyReleased(InputKey.F8))
            {
                return;
            }

            try
            {
                CalradiaTavernDebug.Trace("SubModule", "F8 pressed");
                OpenQuickChatInput();
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("SubModule.OnApplicationTick.F8Open", ex);
            }
        }

        private void OpenQuickChatInput()
        {
            if (_chatInputOpen)
            {
                return;
            }

            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage("[Calradia Tavern] Campaign behavior not ready.", Colors.Red)
                );
                return;
            }

            _chatInputOpen = true;

            try
            {
                InformationManager.ShowTextInquiry(
                    new TextInquiryData(
                        "Global Chat",
                        "Type your message. Sent messages and incoming messages appear in the bottom-left feed.",
                        true,
                        true,
                        "Send",
                        "Cancel",
                        input =>
                        {
                            try
                            {
                                string text = (input ?? string.Empty).Trim();
                                if (text.Length == 0)
                                {
                                    InformationManager.DisplayMessage(
                                        new InformationMessage("[Global Chat] Message cannot be empty.", Colors.Red)
                                    );
                                    return;
                                }

                                string result = behavior.SendChat(text);
                                if (result.StartsWith("Send failed:", StringComparison.OrdinalIgnoreCase))
                                {
                                    InformationManager.DisplayMessage(
                                        new InformationMessage("[Global Chat] " + result, Colors.Red)
                                    );
                                    return;
                                }

                                string sender = string.IsNullOrWhiteSpace(behavior.DisplayName)
                                    ? "Me"
                                    : behavior.DisplayName;
                                InformationManager.DisplayMessage(
                                    new InformationMessage(
                                        CalradiaTavernCampaignBehavior.FormatChatToast(
                                            sender,
                                            text,
                                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                        ),
                                        Colors.Cyan
                                    )
                                );

                                behavior.PullNow();
                            }
                            catch (Exception ex)
                            {
                                CalradiaTavernDebug.ReportException("SubModule.QuickChat.Send", ex);
                                InformationManager.DisplayMessage(
                                    new InformationMessage("[Global Chat] Send exception: " + ex.Message, Colors.Red)
                                );
                            }
                            finally
                            {
                                _chatInputOpen = false;
                            }
                        },
                        () => _chatInputOpen = false,
                        false,
                        null,
                        string.Empty,
                        string.Empty
                    ),
                    false,
                    false
                );
            }
            catch (Exception ex)
            {
                _chatInputOpen = false;
                CalradiaTavernDebug.ReportException("SubModule.QuickChat.Open", ex);
            }
        }

        private static bool IsMapScreenActive()
        {
            ScreenBase top = ScreenManager.TopScreen;
            if (top == null)
            {
                return false;
            }

            string name = top.GetType().Name ?? string.Empty;
            return name.IndexOf("MapScreen", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

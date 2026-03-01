using System;
using CalradiaTavern.Behaviors;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CalradiaTavern
{
    public class SubModule : MBSubModuleBase
    {
        private const string BuildMarker = "CTavern build 2026-03-01 17:15 F8-text-input";
        private const float BackgroundPollIntervalSeconds = 1.5f;
        private bool _chatInputOpen;
        private float _backgroundPollElapsed;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            CalradiaTavernDebug.Initialize();
            CalradiaTavernDebug.Trace("SubModule", "OnSubModuleLoad " + BuildMarker + " " + CalradiaTavernDebug.BuildTag);
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

            if (Input.IsKeyReleased(InputKey.F8))
            {
                CalradiaTavernDebug.Trace("SubModule", "F8 pressed");
                OpenQuickChatInput();
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
                    new InformationMessage("[Calradia Tavern] Campaign behavior not ready.")
                );
                return;
            }

            _chatInputOpen = true;

            try
            {
                InformationManager.ShowTextInquiry(
                    new TextInquiryData(
                        "Calradia Tavern",
                        "Type your global chat message",
                        true,
                        true,
                        "Send",
                        "Cancel",
                        input =>
                        {
                            try
                            {
                                string result = behavior.SendChat(input);
                                bool failed = result.StartsWith("Send failed:", StringComparison.OrdinalIgnoreCase);
                                InformationManager.DisplayMessage(
                                    new InformationMessage(
                                        "[Calradia Tavern] " + result,
                                        failed ? Colors.Red : Colors.Green
                                    )
                                );
                                behavior.PullNow();
                            }
                            catch (Exception ex)
                            {
                                CalradiaTavernDebug.ReportException("SubModule.OpenQuickChatInput.Send", ex);
                                InformationManager.DisplayMessage(
                                    new InformationMessage("[Calradia Tavern] Send exception: " + ex.Message, Colors.Red)
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
                CalradiaTavernDebug.ReportException("SubModule.OpenQuickChatInput.ShowTextInquiry", ex);
                InformationManager.DisplayMessage(
                    new InformationMessage("[Calradia Tavern] Open input failed: " + ex.Message, Colors.Red)
                );
            }
        }
    }
}

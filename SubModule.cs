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
        private const string BuildMarker = "CTavern build 2026-03-02 13:20 f8-persistent-cn-clean";
        private const float BackgroundPollIntervalSeconds = 1.5f;

        private float _backgroundPollElapsed;
        private bool _chatInputOpen;
        private bool _quickChatReopenRequested;
        private string _quickChatReopenDefaultText = string.Empty;

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

            if (_quickChatReopenRequested && !_chatInputOpen)
            {
                string reopenText = _quickChatReopenDefaultText ?? string.Empty;
                _quickChatReopenRequested = false;
                _quickChatReopenDefaultText = string.Empty;
                OpenQuickChatInput(reopenText);
            }

            if (!Input.IsKeyReleased(InputKey.F8))
            {
                return;
            }

            try
            {
                CalradiaTavernDebug.Trace("SubModule", "F8 pressed");
                OpenQuickChatInput(string.Empty);
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("SubModule.OnApplicationTick.F8Open", ex);
            }
        }

        private void OpenQuickChatInput(string defaultInputText)
        {
            if (_chatInputOpen)
            {
                return;
            }

            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage("[卡拉迪亚酒馆] 战役行为尚未就绪。", Colors.Red)
                );
                return;
            }

            _chatInputOpen = true;
            try
            {
                InformationManager.ShowTextInquiry(
                    new TextInquiryData(
                        "全服聊天",
                        "输入消息后点击发送，窗口会继续保留；点击关闭退出。",
                        true,
                        true,
                        "发送",
                        "关闭",
                        input =>
                        {
                            string reopenText = string.Empty;
                            try
                            {
                                string rawText = input ?? string.Empty;
                                string text = rawText.Trim();
                                if (text.Length == 0)
                                {
                                    InformationManager.DisplayMessage(
                                        new InformationMessage("[全服聊天] 消息不能为空。", Colors.Red)
                                    );
                                    reopenText = rawText;
                                    return;
                                }

                                string result = behavior.SendChat(text);
                                if (result.StartsWith("Send failed:", StringComparison.OrdinalIgnoreCase))
                                {
                                    InformationManager.DisplayMessage(
                                        new InformationMessage("[全服聊天] " + result, Colors.Red)
                                    );
                                    reopenText = rawText;
                                    return;
                                }

                                behavior.PullNow();
                                reopenText = string.Empty;
                            }
                            catch (Exception ex)
                            {
                                CalradiaTavernDebug.ReportException("SubModule.QuickChat.Send", ex);
                                InformationManager.DisplayMessage(
                                    new InformationMessage("[全服聊天] 发送异常: " + ex.Message, Colors.Red)
                                );
                                reopenText = input ?? string.Empty;
                            }
                            finally
                            {
                                _chatInputOpen = false;
                                RequestQuickChatReopen(reopenText);
                            }
                        },
                        () =>
                        {
                            _quickChatReopenRequested = false;
                            _quickChatReopenDefaultText = string.Empty;
                            _chatInputOpen = false;
                        },
                        false,
                        null,
                        string.Empty,
                        defaultInputText ?? string.Empty
                    ),
                    true,
                    true
                );
            }
            catch (Exception ex)
            {
                _chatInputOpen = false;
                _quickChatReopenRequested = false;
                _quickChatReopenDefaultText = string.Empty;
                CalradiaTavernDebug.ReportException("SubModule.QuickChat.Open", ex);
            }
        }

        private void RequestQuickChatReopen(string defaultInputText)
        {
            _quickChatReopenRequested = true;
            _quickChatReopenDefaultText = defaultInputText ?? string.Empty;
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

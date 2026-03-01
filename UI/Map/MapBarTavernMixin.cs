using System;
using System.Collections.Generic;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.ViewModels;
using CalradiaTavern.Behaviors;
using TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapBar;
using TaleWorlds.Library;

namespace CalradiaTavern.UI.Map
{
    [ViewModelMixin("RefreshValues", true)]
    internal sealed class MapBarTavernMixin : BaseViewModelMixin<MapBarVM>
    {
        private bool _hasTavernUnread;
        private string _tavernUnreadText = string.Empty;
        private bool _isTavernPanelOpen;
        private string _tavernFeedText = "No messages yet.";
        private string _tavernStatusText = string.Empty;
        private string _tavernInputText = string.Empty;

        public MapBarTavernMixin(MapBarVM vm)
            : base(vm)
        {
            CalradiaTavernCampaignBehavior.StateChanged += OnBehaviorStateChanged;
            RefreshFromBehavior();
        }

        [DataSourceProperty]
        public string TavernButtonText => "聊天";

        [DataSourceProperty]
        public string TavernPanelTitle => "卡拉迪亚酒馆";

        [DataSourceProperty]
        public string TavernSendText => "发送";

        [DataSourceProperty]
        public string TavernRefreshText => "刷新";

        [DataSourceProperty]
        public string TavernCloseText => "关闭";

        [DataSourceProperty]
        public bool HasTavernUnread
        {
            get => _hasTavernUnread;
            set
            {
                if (value != _hasTavernUnread)
                {
                    _hasTavernUnread = value;
                    OnPropertyChangedWithValue(value, nameof(HasTavernUnread));
                }
            }
        }

        [DataSourceProperty]
        public string TavernUnreadText
        {
            get => _tavernUnreadText;
            set
            {
                if (value != _tavernUnreadText)
                {
                    _tavernUnreadText = value;
                    OnPropertyChangedWithValue(value, nameof(TavernUnreadText));
                }
            }
        }

        [DataSourceProperty]
        public bool IsTavernPanelOpen
        {
            get => _isTavernPanelOpen;
            set
            {
                if (value != _isTavernPanelOpen)
                {
                    _isTavernPanelOpen = value;
                    OnPropertyChangedWithValue(value, nameof(IsTavernPanelOpen));
                }
            }
        }

        [DataSourceProperty]
        public string TavernFeedText
        {
            get => _tavernFeedText;
            set
            {
                if (value != _tavernFeedText)
                {
                    _tavernFeedText = value;
                    OnPropertyChangedWithValue(value, nameof(TavernFeedText));
                }
            }
        }

        [DataSourceProperty]
        public string TavernStatusText
        {
            get => _tavernStatusText;
            set
            {
                if (value != _tavernStatusText)
                {
                    _tavernStatusText = value;
                    OnPropertyChangedWithValue(value, nameof(TavernStatusText));
                }
            }
        }

        [DataSourceProperty]
        public string TavernInputText
        {
            get => _tavernInputText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _tavernInputText)
                {
                    _tavernInputText = next;
                    OnPropertyChangedWithValue(next, nameof(TavernInputText));
                }
            }
        }

        [DataSourceMethod]
        public void ExecuteToggleCalradiaTavernPanel()
        {
            IsTavernPanelOpen = !IsTavernPanelOpen;

            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior != null && IsTavernPanelOpen)
            {
                behavior.MarkChatRead();
                behavior.PullNow();
            }

            RefreshFromBehavior();
        }

        [DataSourceMethod]
        public void ExecuteTavernClose()
        {
            IsTavernPanelOpen = false;
            RefreshFromBehavior();
        }

        [DataSourceMethod]
        public void ExecuteTavernRefresh()
        {
            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                TavernStatusText = "Campaign behavior is not ready.";
                return;
            }

            TavernStatusText = behavior.PullNow();
            RefreshFromBehavior();
        }

        [DataSourceMethod]
        public void ExecuteTavernSendFromInput()
        {
            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            if (behavior == null)
            {
                TavernStatusText = "Campaign behavior is not ready.";
                return;
            }

            string input = (TavernInputText ?? string.Empty).Trim();
            if (input.Length == 0)
            {
                TavernStatusText = "请输入聊天内容。";
                return;
            }

            string result = behavior.SendChat(input);
            TavernStatusText = result;
            if (result.StartsWith("Sent", StringComparison.OrdinalIgnoreCase))
            {
                TavernInputText = string.Empty;
                behavior.PullNow();
            }

            RefreshFromBehavior();
        }

        public override void OnRefresh()
        {
            base.OnRefresh();
            RefreshFromBehavior();
        }

        public override void OnFinalize()
        {
            CalradiaTavernCampaignBehavior.StateChanged -= OnBehaviorStateChanged;
            base.OnFinalize();
        }

        private void OnBehaviorStateChanged()
        {
            RefreshFromBehavior();
        }

        private void RefreshFromBehavior()
        {
            CalradiaTavernCampaignBehavior behavior = CalradiaTavernCampaignBehavior.Instance;
            int unread = behavior?.UnreadChatCount ?? 0;
            HasTavernUnread = unread > 0;
            TavernUnreadText = unread > 99 ? "99+" : unread.ToString();

            if (behavior == null)
            {
                TavernFeedText = "No campaign behavior. Load a singleplayer campaign save.";
                return;
            }

            IReadOnlyList<TavernChatLine> lines = behavior.GetRecentChatLines(24);
            if (lines == null || lines.Count == 0)
            {
                TavernFeedText = "暂无聊天消息。";
                return;
            }

            List<string> rows = new List<string>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                TavernChatLine line = lines[i];
                string sender = string.IsNullOrWhiteSpace(line.PlayerName)
                    ? "Anonymous"
                    : line.PlayerName.Trim();
                string text = (line.Text ?? string.Empty).Trim();
                if (text.Length > 90)
                {
                    text = text.Substring(0, 90) + "...";
                }

                rows.Add("[" + ToLocalTimeText(line.UnixTimeMs) + "] " + sender + ": " + text);
            }

            TavernFeedText = string.Join("\n", rows);
        }

        private static string ToLocalTimeText(long unixMs)
        {
            if (unixMs <= 0)
            {
                return "--:--";
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm");
            }
            catch
            {
                return "--:--";
            }
        }
    }
}

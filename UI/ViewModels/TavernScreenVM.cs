using System;
using System.Collections.Generic;
using CalradiaTavern.Behaviors;
using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal sealed class TavernScreenVM : ViewModel
    {
        private readonly Action _onClose;
        private CalradiaTavernCampaignBehavior _behavior;
        private float _refreshElapsed;

        private string _chatInputText = string.Empty;
        private string _nameInputText = string.Empty;
        private string _statusText = string.Empty;

        public TavernScreenVM(Action onClose)
        {
            _onClose = onClose;
            ChatLines = new MBBindingList<TavernChatLineVM>();
        }

        [DataSourceProperty]
        public string TitleText => "Calradia Tavern";

        [DataSourceProperty]
        public string SendChatButtonText => "Send";

        [DataSourceProperty]
        public string ApplyNameButtonText => "Set Name";

        [DataSourceProperty]
        public string CloseButtonText => "Close";

        [DataSourceProperty]
        public string ChatInputText
        {
            get => _chatInputText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _chatInputText)
                {
                    _chatInputText = next;
                    OnPropertyChangedWithValue(next, nameof(ChatInputText));
                }
            }
        }

        [DataSourceProperty]
        public string NameInputText
        {
            get => _nameInputText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _nameInputText)
                {
                    _nameInputText = next;
                    OnPropertyChangedWithValue(next, nameof(NameInputText));
                }
            }
        }

        [DataSourceProperty]
        public string StatusText
        {
            get => _statusText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _statusText)
                {
                    _statusText = next;
                    OnPropertyChangedWithValue(next, nameof(StatusText));
                }
            }
        }

        [DataSourceProperty]
        public MBBindingList<TavernChatLineVM> ChatLines { get; }

        public void OnActivated()
        {
            _behavior = CalradiaTavernCampaignBehavior.Instance;
            if (_behavior == null)
            {
                StatusText = "Behavior not found in current save.";
                return;
            }

            NameInputText = _behavior.DisplayName ?? string.Empty;
            _behavior.MarkChatRead();
            StatusText = _behavior.PullNow();
            RefreshChatLines();
        }

        public void OnDeactivated()
        {
            _behavior?.MarkChatRead();
        }

        public void Tick(float dt)
        {
            if (_behavior == null)
            {
                return;
            }

            _behavior.MarkChatRead();
            _refreshElapsed += Math.Max(0f, dt);
            if (_refreshElapsed < 0.8f)
            {
                return;
            }

            _refreshElapsed = 0f;
            RefreshChatLines();
        }

        public void ExecuteSendChat()
        {
            if (_behavior == null)
            {
                StatusText = "Behavior not ready.";
                return;
            }

            string input = (ChatInputText ?? string.Empty).Trim();
            if (input.Length == 0)
            {
                StatusText = "Message cannot be empty.";
                return;
            }

            string result = _behavior.SendChat(input);
            StatusText = result;
            if (!result.StartsWith("Send failed:", StringComparison.OrdinalIgnoreCase))
            {
                ChatInputText = string.Empty;
            }

            RefreshChatLines();
        }

        public void ExecuteApplyName()
        {
            if (_behavior == null)
            {
                StatusText = "Behavior not ready.";
                return;
            }

            StatusText = _behavior.SetDisplayName(NameInputText);
        }

        public void ExecuteRefreshAll()
        {
            if (_behavior == null)
            {
                StatusText = "Behavior not ready.";
                return;
            }

            StatusText = _behavior.PullNow();
            RefreshChatLines();
        }

        public void ExecuteClose()
        {
            _onClose?.Invoke();
        }

        private void RefreshChatLines()
        {
            if (_behavior == null)
            {
                return;
            }

            IReadOnlyList<TavernChatLine> lines = _behavior.GetRecentChatLines(140);
            ChatLines.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                TavernChatLine line = lines[i];
                string sender = string.IsNullOrWhiteSpace(line.PlayerName)
                    ? (line.IsSelf ? (_behavior.DisplayName ?? "Me") : "Anonymous")
                    : line.PlayerName.Trim();

                ChatLines.Add(new TavernChatLineVM(sender + ": " + (line.Text ?? string.Empty), line.IsSelf));
            }
        }
    }
}

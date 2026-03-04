using System;
using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal sealed class TavernOnlinePlayerVM : ViewModel
    {
        private readonly Action<TavernOnlinePlayerVM> _onBlock;
        private string _nameText;
        private string _blockButtonText = "拉黑";
        private bool _isBlockEnabled = true;

        public TavernOnlinePlayerVM(
            string nameText,
            bool isBlockEnabled,
            Action<TavernOnlinePlayerVM> onBlock
        )
        {
            _nameText = string.IsNullOrWhiteSpace(nameText) ? "Anonymous" : nameText.Trim();
            _isBlockEnabled = isBlockEnabled;
            _onBlock = onBlock;
        }

        [DataSourceProperty]
        public string NameText
        {
            get => _nameText;
            set
            {
                string next = string.IsNullOrWhiteSpace(value) ? "Anonymous" : value.Trim();
                if (next != _nameText)
                {
                    _nameText = next;
                    OnPropertyChangedWithValue(next, nameof(NameText));
                }
            }
        }

        [DataSourceProperty]
        public string BlockButtonText
        {
            get => _blockButtonText;
            set
            {
                string next = string.IsNullOrWhiteSpace(value) ? "拉黑" : value.Trim();
                if (next != _blockButtonText)
                {
                    _blockButtonText = next;
                    OnPropertyChangedWithValue(next, nameof(BlockButtonText));
                }
            }
        }

        [DataSourceProperty]
        public bool IsBlockEnabled
        {
            get => _isBlockEnabled;
            set
            {
                if (value != _isBlockEnabled)
                {
                    _isBlockEnabled = value;
                    OnPropertyChangedWithValue(value, nameof(IsBlockEnabled));
                }
            }
        }

        public void ExecuteBlock()
        {
            if (!_isBlockEnabled)
            {
                return;
            }

            _onBlock?.Invoke(this);
        }
    }
}

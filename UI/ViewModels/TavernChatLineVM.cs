using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal sealed class TavernChatLineVM : ViewModel
    {
        private readonly string _messageId;
        private string _lineText;
        private bool _isSelf;

        public TavernChatLineVM(string messageId, string lineText, bool isSelf)
        {
            _messageId = messageId ?? string.Empty;
            _lineText = lineText ?? string.Empty;
            _isSelf = isSelf;
        }

        public string MessageId => _messageId;

        [DataSourceProperty]
        public string LineText
        {
            get => _lineText;
            set
            {
                if (value != _lineText)
                {
                    _lineText = value;
                    OnPropertyChangedWithValue(value, nameof(LineText));
                }
            }
        }

        [DataSourceProperty]
        public bool IsSelf
        {
            get => _isSelf;
            set
            {
                if (value != _isSelf)
                {
                    _isSelf = value;
                    OnPropertyChangedWithValue(value, nameof(IsSelf));
                }
            }
        }
    }
}

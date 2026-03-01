using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal sealed class TavernChatLineVM : ViewModel
    {
        private string _lineText;
        private bool _isSelf;

        public TavernChatLineVM(string lineText, bool isSelf)
        {
            _lineText = lineText ?? string.Empty;
            _isSelf = isSelf;
        }

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

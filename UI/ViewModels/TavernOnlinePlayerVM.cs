using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal sealed class TavernOnlinePlayerVM : ViewModel
    {
        private string _nameText;

        public TavernOnlinePlayerVM(string nameText)
        {
            _nameText = string.IsNullOrWhiteSpace(nameText) ? "Anonymous" : nameText.Trim();
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
    }
}

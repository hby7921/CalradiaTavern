using System;
using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal sealed class TavernMarketOwnedItemVM : ViewModel
    {
        private readonly Action<TavernMarketOwnedItemVM> _onSelect;
        private readonly string _itemId;
        private string _lineText;

        public TavernMarketOwnedItemVM(
            string itemId,
            string lineText,
            Action<TavernMarketOwnedItemVM> onSelect
        )
        {
            _onSelect = onSelect;
            _itemId = itemId ?? string.Empty;
            _lineText = lineText ?? string.Empty;
        }

        [DataSourceProperty]
        public string ItemId => _itemId;

        [DataSourceProperty]
        public string LineText
        {
            get => _lineText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _lineText)
                {
                    _lineText = next;
                    OnPropertyChangedWithValue(next, nameof(LineText));
                }
            }
        }

        public void ExecuteSelect()
        {
            _onSelect?.Invoke(this);
        }
    }
}

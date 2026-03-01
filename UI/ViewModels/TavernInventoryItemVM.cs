using System;
using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal sealed class TavernInventoryItemVM : ViewModel
    {
        private readonly Action<TavernInventoryItemVM> _onSelect;
        private bool _isSelected;
        private string _displayText;

        public TavernInventoryItemVM(
            string itemId,
            string itemName,
            int count,
            Action<TavernInventoryItemVM> onSelect
        )
        {
            ItemId = itemId;
            ItemName = itemName;
            Count = count;
            _displayText = $"{itemName} ({count})";
            _onSelect = onSelect;
        }

        public string ItemId { get; }

        public string ItemName { get; }

        public int Count { get; }

        [DataSourceProperty]
        public string DisplayText
        {
            get => _displayText;
            set
            {
                if (value != _displayText)
                {
                    _displayText = value;
                    OnPropertyChangedWithValue(value, nameof(DisplayText));
                }
            }
        }

        [DataSourceProperty]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (value != _isSelected)
                {
                    _isSelected = value;
                    OnPropertyChangedWithValue(value, nameof(IsSelected));
                }
            }
        }

        public void ExecuteSelect()
        {
            _onSelect?.Invoke(this);
        }
    }
}

using System;
using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal enum TavernMarketActionKind
    {
        None = 0,
        Buy = 1,
        Cancel = 2,
    }

    internal sealed class TavernMarketListingVM : ViewModel
    {
        private readonly Action<TavernMarketListingVM> _onPrimaryAction;
        private string _listingId;
        private string _itemText;
        private string _sellerText;
        private string _categoryText;
        private string _priceText;
        private string _timeText;
        private string _actionText;
        private bool _isActionEnabled;

        public TavernMarketListingVM(
            string listingId,
            string itemText,
            string sellerText,
            string categoryText,
            string priceText,
            string timeText,
            string actionText,
            bool isActionEnabled,
            TavernMarketActionKind actionKind,
            Action<TavernMarketListingVM> onPrimaryAction
        )
        {
            _listingId = listingId ?? string.Empty;
            _itemText = itemText ?? string.Empty;
            _sellerText = sellerText ?? string.Empty;
            _categoryText = categoryText ?? string.Empty;
            _priceText = priceText ?? string.Empty;
            _timeText = timeText ?? string.Empty;
            _actionText = actionText ?? string.Empty;
            _isActionEnabled = isActionEnabled;
            ActionKind = actionKind;
            _onPrimaryAction = onPrimaryAction;
        }

        [DataSourceProperty]
        public string ListingId
        {
            get => _listingId;
            set
            {
                string next = value ?? string.Empty;
                if (next != _listingId)
                {
                    _listingId = next;
                    OnPropertyChangedWithValue(next, nameof(ListingId));
                }
            }
        }

        public TavernMarketActionKind ActionKind { get; }

        [DataSourceProperty]
        public string ItemText
        {
            get => _itemText;
            set
            {
                if (value != _itemText)
                {
                    _itemText = value ?? string.Empty;
                    OnPropertyChangedWithValue(_itemText, nameof(ItemText));
                }
            }
        }

        [DataSourceProperty]
        public string SellerText
        {
            get => _sellerText;
            set
            {
                if (value != _sellerText)
                {
                    _sellerText = value ?? string.Empty;
                    OnPropertyChangedWithValue(_sellerText, nameof(SellerText));
                }
            }
        }

        [DataSourceProperty]
        public string CategoryText
        {
            get => _categoryText;
            set
            {
                if (value != _categoryText)
                {
                    _categoryText = value ?? string.Empty;
                    OnPropertyChangedWithValue(_categoryText, nameof(CategoryText));
                }
            }
        }

        [DataSourceProperty]
        public string PriceText
        {
            get => _priceText;
            set
            {
                if (value != _priceText)
                {
                    _priceText = value ?? string.Empty;
                    OnPropertyChangedWithValue(_priceText, nameof(PriceText));
                }
            }
        }

        [DataSourceProperty]
        public string TimeText
        {
            get => _timeText;
            set
            {
                if (value != _timeText)
                {
                    _timeText = value ?? string.Empty;
                    OnPropertyChangedWithValue(_timeText, nameof(TimeText));
                }
            }
        }

        [DataSourceProperty]
        public string ActionText
        {
            get => _actionText;
            set
            {
                if (value != _actionText)
                {
                    _actionText = value ?? string.Empty;
                    OnPropertyChangedWithValue(_actionText, nameof(ActionText));
                }
            }
        }

        [DataSourceProperty]
        public bool IsActionEnabled
        {
            get => _isActionEnabled;
            set
            {
                if (value != _isActionEnabled)
                {
                    _isActionEnabled = value;
                    OnPropertyChangedWithValue(value, nameof(IsActionEnabled));
                }
            }
        }

        public void ExecutePrimaryAction()
        {
            CalradiaTavernDebug.Trace(
                "VM.Row",
                "ExecutePrimaryAction id=" + ListingId + " enabled=" + IsActionEnabled
            );
            if (!IsActionEnabled)
            {
                return;
            }

            _onPrimaryAction?.Invoke(this);
        }
    }
}

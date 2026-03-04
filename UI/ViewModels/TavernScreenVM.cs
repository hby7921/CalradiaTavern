using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CalradiaTavern.Behaviors;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace CalradiaTavern.UI.ViewModels
{
    internal sealed class TavernScreenVM : ViewModel
    {
        private const int TradePageSize = 20;
        private const int PlayerListPageSize = 20;
        private const string LocalTradeBotName = "交易机器人";

        private readonly Action _onClose;
        private readonly Dictionary<string, TavernInventoryEntry> _inventoryById =
            new Dictionary<string, TavernInventoryEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TavernMarketActionKind> _marketPrimaryActionById =
            new Dictionary<string, TavernMarketActionKind>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _playerNameBySelectToken =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _allOnlinePlayerNames = new List<string>();
        private readonly List<string> _allTradeTargetPlayerNames = new List<string>();

        private CalradiaTavernCampaignBehavior _behavior;
        private float _refreshElapsed;
        private float _pullElapsed;
        private int _selectedTab;
        private int _chatVisualVersion;
        private int _marketPageIndex;
        private int _marketTotalPages = 1;
        private int _marketFilteredCount;
        private int _marketVisibleCount;
        private int _onlinePlayersPageIndex;
        private int _onlinePlayersTotalPages = 1;
        private int _tradePlayersPageIndex;
        private int _tradePlayersTotalPages = 1;
        private long _nextChatOrderDiagLogMs;

        private string _chatInputText = string.Empty;
        private string _nameInputText = string.Empty;
        private string _statusText = string.Empty;
        private string _publishSearchText = string.Empty;
        private string _publishItemIdText = string.Empty;
        private string _publishPriceText = "1";
        private string _publishSelectedItemText = string.Empty;
        private string _marketSearchKeyword = string.Empty;
        private string _marketSearchPopupText = string.Empty;
        private string _selectedTargetPlayerName = string.Empty;

        private bool _isPublishSelectorPopupVisible;
        private bool _isPublishPricePopupVisible;
        private bool _isMarketSearchPopupVisible;

        public TavernScreenVM(Action onClose)
        {
            _onClose = onClose;
            ChatLines = new MBBindingList<TavernChatLineVM>();
            OnlinePlayers = new MBBindingList<TavernOnlinePlayerVM>();
            MarketListings = new MBBindingList<TavernMarketListingVM>();
            MyMarketListings = new MBBindingList<TavernMarketListingVM>();
            PublishableItems = new MBBindingList<TavernMarketOwnedItemVM>();
        }

        [DataSourceProperty]
        public string TitleText => "卡拉迪亚酒馆";

        [DataSourceProperty]
        public string CloseButtonText => "关闭";

        [DataSourceProperty]
        public string ChatTabText => "聊天";

        [DataSourceProperty]
        public string MarketTabText => "交易";

        [DataSourceProperty]
        public string RefreshButtonText => "刷新";

        [DataSourceProperty]
        public string OnlinePlayersTitleText => "在线玩家";

        [DataSourceProperty]
        public string ApplyNameButtonText => "更新名字";

        [DataSourceProperty]
        public string ChatAreaTitleText => "酒馆情报交流";

        [DataSourceProperty]
        public string SendChatButtonText => "发送";

        [DataSourceProperty]
        public string MarketSectionTitleText => "玩家交易";

        [DataSourceProperty]
        public string PublishableItemsTitleText => "背包物品";

        [DataSourceProperty]
        public string MarketListingsTitleText => "可发送物品";

        [DataSourceProperty]
        public string MyListingsTitleText => "目标玩家";

        [DataSourceProperty]
        public string MarketSearchHintText => "输入物品名称或ID";

        [DataSourceProperty]
        public string PublishPriceHintText => "发送数量";

        [DataSourceProperty]
        public string SearchButtonText => "搜索";

        [DataSourceProperty]
        public string ClearButtonText => "清空";

        [DataSourceProperty]
        public string PublishButtonText => "发送";

        [DataSourceProperty]
        public string MarketRulesText
        {
            get
            {
                return "仅显示原版武器/护甲/旗帜，可关键词搜索。";
            }
        }

        [DataSourceProperty]
        public string MyListingsLimitText =>
            string.IsNullOrWhiteSpace(_selectedTargetPlayerName)
                ? "目标玩家：未选择"
                : "目标玩家：" + _selectedTargetPlayerName;

        [DataSourceProperty]
        public string MarketColNameText => "物品";

        [DataSourceProperty]
        public string MarketColTypeText => "数量";

        [DataSourceProperty]
        public string MarketColListedAtText => "物品ID";

        [DataSourceProperty]
        public string MarketColPriceText => "发送给";

        [DataSourceProperty]
        public string MarketColActionText => "状态";

        [DataSourceProperty]
        public string MarketColOpText => "操作";

        [DataSourceProperty]
        public string OpenPublishButtonText => "选择物品";

        [DataSourceProperty]
        public string ConfirmButtonText => "确定";

        [DataSourceProperty]
        public string CancelButtonText => "取消";

        [DataSourceProperty]
        public string PrevPageText => "<";

        [DataSourceProperty]
        public string NextPageText => ">";

        [DataSourceProperty]
        public string PublishSelectorTitleText => "选择要发送的物品";

        [DataSourceProperty]
        public string PublishSelectorSearchHintText => "搜索......";

        [DataSourceProperty]
        public string PublishSelectorEmptyText => "未找到可发送物品";

        [DataSourceProperty]
        public string PublishPriceTitleText => "输入发送数量";

        [DataSourceProperty]
        public string PublishSelectedItemLabelText => "物品";

        [DataSourceProperty]
        public string MarketSearchTitleText => "搜索物品";

        [DataSourceProperty]
        public string MarketSearchCurrentText
        {
            get
            {
                string target = string.IsNullOrWhiteSpace(_selectedTargetPlayerName)
                    ? "未选择"
                    : _selectedTargetPlayerName;
                string filter = string.IsNullOrWhiteSpace(_marketSearchKeyword)
                    ? "全部"
                    : _marketSearchKeyword;
                return "目标: " + target + " | 筛选: " + filter;
            }
        }

        [DataSourceProperty]
        public string MarketResultSummaryText =>
            "结果: "
            + _marketFilteredCount.ToString(CultureInfo.InvariantCulture)
            + " 条 | 当前页: "
            + _marketVisibleCount.ToString(CultureInfo.InvariantCulture)
            + " 条";

        [DataSourceProperty]
        public string MarketPageText => (_marketPageIndex + 1).ToString(CultureInfo.InvariantCulture)
            + " / "
            + Math.Max(1, _marketTotalPages).ToString(CultureInfo.InvariantCulture);

        [DataSourceProperty]
        public bool CanPrevMarketPage => _marketPageIndex > 0;

        [DataSourceProperty]
        public bool CanNextMarketPage => _marketPageIndex + 1 < Math.Max(1, _marketTotalPages);

        [DataSourceProperty]
        public string OnlinePlayersPageText =>
            (_onlinePlayersPageIndex + 1).ToString(CultureInfo.InvariantCulture)
            + " / "
            + Math.Max(1, _onlinePlayersTotalPages).ToString(CultureInfo.InvariantCulture);

        [DataSourceProperty]
        public bool CanPrevOnlinePlayersPage => _onlinePlayersPageIndex > 0;

        [DataSourceProperty]
        public bool CanNextOnlinePlayersPage =>
            _onlinePlayersPageIndex + 1 < Math.Max(1, _onlinePlayersTotalPages);

        [DataSourceProperty]
        public string TradePlayersPageText =>
            (_tradePlayersPageIndex + 1).ToString(CultureInfo.InvariantCulture)
            + " / "
            + Math.Max(1, _tradePlayersTotalPages).ToString(CultureInfo.InvariantCulture);

        [DataSourceProperty]
        public bool CanPrevTradePlayersPage => _tradePlayersPageIndex > 0;

        [DataSourceProperty]
        public bool CanNextTradePlayersPage =>
            _tradePlayersPageIndex + 1 < Math.Max(1, _tradePlayersTotalPages);

        [DataSourceProperty]
        public bool IsChatTab => _selectedTab == 0;

        [DataSourceProperty]
        public bool IsMarketTab => _selectedTab == 1;

        [DataSourceProperty]
        public bool IsPublishSelectorPopupVisible
        {
            get => _isPublishSelectorPopupVisible;
            set
            {
                if (value != _isPublishSelectorPopupVisible)
                {
                    _isPublishSelectorPopupVisible = value;
                    OnPropertyChangedWithValue(value, nameof(IsPublishSelectorPopupVisible));
                }
            }
        }

        [DataSourceProperty]
        public bool IsPublishPricePopupVisible
        {
            get => _isPublishPricePopupVisible;
            set
            {
                if (value != _isPublishPricePopupVisible)
                {
                    _isPublishPricePopupVisible = value;
                    OnPropertyChangedWithValue(value, nameof(IsPublishPricePopupVisible));
                }
            }
        }

        [DataSourceProperty]
        public bool IsMarketSearchPopupVisible
        {
            get => _isMarketSearchPopupVisible;
            set
            {
                if (value != _isMarketSearchPopupVisible)
                {
                    _isMarketSearchPopupVisible = value;
                    OnPropertyChangedWithValue(value, nameof(IsMarketSearchPopupVisible));
                }
            }
        }

        [DataSourceProperty]
        public bool HasPublishableItems => PublishableItems.Count > 0;

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
        public string MarketSearchText
        {
            get => _marketSearchKeyword;
            set
            {
                string next = (value ?? string.Empty).Trim();
                if (next != _marketSearchKeyword)
                {
                    _marketSearchKeyword = next;
                    _marketPageIndex = 0;
                    OnPropertyChangedWithValue(next, nameof(MarketSearchText));
                    OnPropertyChanged(nameof(MarketSearchCurrentText));
                    RefreshTradeInventory();
                }
            }
        }

        [DataSourceProperty]
        public string MarketSearchPopupText
        {
            get => _marketSearchPopupText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _marketSearchPopupText)
                {
                    _marketSearchPopupText = next;
                    OnPropertyChangedWithValue(next, nameof(MarketSearchPopupText));
                }
            }
        }

        [DataSourceProperty]
        public string PublishSearchText
        {
            get => _publishSearchText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _publishSearchText)
                {
                    _publishSearchText = next;
                    OnPropertyChangedWithValue(next, nameof(PublishSearchText));
                    if (IsPublishSelectorPopupVisible)
                    {
                        RefreshPublishableItems();
                    }
                }
            }
        }

        [DataSourceProperty]
        public string PublishItemIdText
        {
            get => _publishItemIdText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _publishItemIdText)
                {
                    _publishItemIdText = next;
                    OnPropertyChangedWithValue(next, nameof(PublishItemIdText));
                }
            }
        }

        [DataSourceProperty]
        public string PublishPriceText
        {
            get => _publishPriceText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _publishPriceText)
                {
                    _publishPriceText = next;
                    OnPropertyChangedWithValue(next, nameof(PublishPriceText));
                }
            }
        }

        [DataSourceProperty]
        public string PublishSelectedItemText
        {
            get => _publishSelectedItemText;
            set
            {
                string next = value ?? string.Empty;
                if (next != _publishSelectedItemText)
                {
                    _publishSelectedItemText = next;
                    OnPropertyChangedWithValue(next, nameof(PublishSelectedItemText));
                }
            }
        }

        [DataSourceProperty]
        public int ChatVisualVersion => _chatVisualVersion;

        [DataSourceProperty]
        public MBBindingList<TavernChatLineVM> ChatLines { get; }

        [DataSourceProperty]
        public MBBindingList<TavernOnlinePlayerVM> OnlinePlayers { get; }

        [DataSourceProperty]
        public MBBindingList<TavernMarketListingVM> MarketListings { get; }

        [DataSourceProperty]
        public MBBindingList<TavernMarketListingVM> MyMarketListings { get; }

        [DataSourceProperty]
        public MBBindingList<TavernMarketOwnedItemVM> PublishableItems { get; }

        public void OnActivated()
        {
            _behavior = CalradiaTavernCampaignBehavior.Instance;
            _pullElapsed = 0f;
            _refreshElapsed = 0f;
            _marketPageIndex = 0;
            _marketTotalPages = 1;
            _marketPrimaryActionById.Clear();
            _playerNameBySelectToken.Clear();
            _inventoryById.Clear();
            IsPublishSelectorPopupVisible = false;
            IsPublishPricePopupVisible = false;
            IsMarketSearchPopupVisible = false;

            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            _behavior.SetPresenceActive(true);
            NameInputText = _behavior.DisplayName ?? string.Empty;
            _behavior.MarkChatRead();
            _behavior.PullNow();
            StatusText = string.Empty;
            OnPropertyChanged(nameof(MarketRulesText));
            RefreshAllData(forceInventory: true);
        }

        public void OnDeactivated()
        {
            _behavior?.SetPresenceActive(false);
            _behavior?.MarkChatRead();
            _behavior?.ClearLocalChatCache();
            ChatLines.Clear();
            _chatVisualVersion++;
            OnPropertyChanged(nameof(ChatVisualVersion));
            IsPublishSelectorPopupVisible = false;
            IsPublishPricePopupVisible = false;
            IsMarketSearchPopupVisible = false;
        }

        public void Tick(float dt)
        {
            if (_behavior == null)
            {
                return;
            }

            _behavior.MarkChatRead();
            _pullElapsed += Math.Max(0f, dt);
            if (_pullElapsed >= 1.25f)
            {
                _pullElapsed = 0f;
                _behavior.PullNow();
            }

            _refreshElapsed += Math.Max(0f, dt);
            if (_refreshElapsed < 0.45f)
            {
                return;
            }

            _refreshElapsed = 0f;
            bool refreshInventory = IsMarketTab || IsPublishSelectorPopupVisible;
            RefreshAllData(refreshInventory);
        }

        public void ExecuteSetTab(string tabParameter)
        {
            if (!int.TryParse(tabParameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tab))
            {
                return;
            }

            int next = Math.Max(0, Math.Min(1, tab));
            if (next == _selectedTab)
            {
                return;
            }

            _selectedTab = next;
            OnPropertyChanged(nameof(IsChatTab));
            OnPropertyChanged(nameof(IsMarketTab));

            if (!IsMarketTab)
            {
                IsPublishSelectorPopupVisible = false;
                IsPublishPricePopupVisible = false;
                IsMarketSearchPopupVisible = false;
                return;
            }

            _marketPageIndex = 0;
            RefreshMarketData(refreshInventory: true);
        }

        public void ExecuteSendChat()
        {
            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            string defaultInput = ChatInputText ?? string.Empty;
            InformationManager.ShowTextInquiry(
                new TextInquiryData(
                    "酒馆聊天",
                    "输入消息后发送。",
                    true,
                    true,
                    "发送",
                    "取消",
                    input =>
                    {
                        string text = (input ?? string.Empty).Trim();
                        if (text.Length == 0)
                        {
                            StatusText = "消息不能为空。";
                            return;
                        }

                        string result = _behavior.SendChat(text);
                        bool accepted = result.IndexOf(
                            "sending in background",
                            StringComparison.OrdinalIgnoreCase
                        ) >= 0;
                        StatusText = accepted ? string.Empty : result;
                        ChatInputText = accepted ? string.Empty : text;
                        RefreshChatLines();
                        RefreshOnlinePlayers();
                    },
                    () => { },
                    false,
                    null,
                    string.Empty,
                    defaultInput
                ),
                true,
                true
            );
        }

        public void ExecuteApplyName()
        {
            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            StatusText = _behavior.SetDisplayName(NameInputText);
            RefreshOnlinePlayers();
        }

        public void ExecuteBlockPlayer(string playerName)
        {
            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            string target = (playerName ?? string.Empty).Trim();
            if (target.Length == 0)
            {
                return;
            }

            StatusText = _behavior.BlockPlayer(target);
            RefreshOnlinePlayers();
        }

        public void ExecuteRefreshAll()
        {
            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            _behavior.PullNow();
            StatusText = string.Empty;
            RefreshAllData(forceInventory: true);
        }

        public void ExecuteOpenPublishPopup()
        {
            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedTargetPlayerName))
            {
                StatusText = "请先在左侧选择目标玩家。";
                return;
            }

            PublishItemIdText = string.Empty;
            PublishSelectedItemText = string.Empty;
            PublishPriceText = "1";
            PublishSearchText = string.Empty;
            IsPublishPricePopupVisible = false;
            RefreshPublishableItems();
            IsPublishSelectorPopupVisible = true;
            StatusText = PublishableItems.Count > 0 ? "在上方输入关键词，或直接选择下方物品。" : "没有可发送物品。";
        }

        public void ExecuteClosePublishPopup()
        {
            IsPublishSelectorPopupVisible = false;
            IsPublishPricePopupVisible = false;
        }

        public void ExecuteSearchPublishableItems()
        {
            ExecuteApplyPublishSearch();
        }

        public void ExecuteApplyPublishSearch()
        {
            RefreshPublishableItems();
            if (!IsPublishSelectorPopupVisible)
            {
                IsPublishSelectorPopupVisible = true;
            }

            StatusText = PublishableItems.Count > 0
                ? "找到 " + PublishableItems.Count.ToString(CultureInfo.InvariantCulture) + " 个匹配物品。"
                : "没有找到匹配物品。";
        }

        public void ExecuteOpenPublishSearchPopup()
        {
            ExecuteOpenPublishSearchPopup(openSelectorAfterApply: true);
        }

        private void ExecuteOpenPublishSearchPopup(bool openSelectorAfterApply)
        {
            string defaultInput = _publishSearchText ?? string.Empty;
            InformationManager.ShowTextInquiry(
                new TextInquiryData(
                    "搜索背包物品",
                    "输入关键词后确定，留空显示全部。",
                    true,
                    true,
                    "确定",
                    "取消",
                    input =>
                    {
                        PublishSearchText = (input ?? string.Empty).Trim();
                        RefreshPublishableItems();
                        if (openSelectorAfterApply)
                        {
                            IsPublishPricePopupVisible = false;
                            IsPublishSelectorPopupVisible = true;
                        }

                        int matchedCount = PublishableItems.Count;
                        StatusText = matchedCount > 0
                            ? "找到 " + matchedCount.ToString(CultureInfo.InvariantCulture) + " 个匹配物品。"
                            : "没有找到匹配物品。";
                    },
                    () => { },
                    false,
                    null,
                    string.Empty,
                    defaultInput
                ),
                true,
                true
            );
        }

        public void ExecuteSelectPublishItem(string itemId)
        {
            string normalized = (itemId ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return;
            }

            PublishItemIdText = normalized;
            PublishSelectedItemText = ResolvePublishItemDisplayText(normalized);
            PublishPriceText = "1";
            IsPublishSelectorPopupVisible = false;
            IsPublishPricePopupVisible = true;
        }

        public void ExecuteCancelPublishPrice()
        {
            CalradiaTavernDebug.Trace("VM", "ExecuteCancelPublishPrice");
            IsPublishPricePopupVisible = false;
        }

        public void ExecuteConfirmPublishPrice()
        {
            CalradiaTavernDebug.Trace("VM", "ExecuteConfirmPublishPrice clicked");
            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            string target = (_selectedTargetPlayerName ?? string.Empty).Trim();
            if (target.Length == 0)
            {
                StatusText = "请先选择目标玩家。";
                return;
            }

            string itemId = (PublishItemIdText ?? string.Empty).Trim();
            if (itemId.Length == 0)
            {
                StatusText = "请选择要发送的物品。";
                return;
            }

            if (
                !int.TryParse(PublishPriceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                || count <= 0
            )
            {
                StatusText = "数量必须是正整数。";
                return;
            }

            CalradiaTavernDebug.Trace(
                "VM",
                "ExecuteConfirmPublishPrice target="
                    + target
                    + " item="
                    + itemId
                    + " count="
                    + count.ToString(CultureInfo.InvariantCulture)
            );
            StatusText = _behavior.SendItemToPlayer(target, itemId, count);
            CalradiaTavernDebug.Trace("VM", "ExecuteConfirmPublishPrice result=" + StatusText);
            _behavior.PullNow();
            RefreshMarketData(refreshInventory: true);

            if (IsTradeAccepted(StatusText))
            {
                IsPublishPricePopupVisible = false;
                IsPublishSelectorPopupVisible = false;
                PublishSearchText = string.Empty;
                PublishItemIdText = string.Empty;
                PublishSelectedItemText = string.Empty;
                PublishPriceText = "1";
            }
        }

        public void ExecuteOpenMarketSearchPopup()
        {
            CalradiaTavernDebug.Trace("VM", "ExecuteOpenMarketSearchPopup");
            string defaultInput = _marketSearchKeyword ?? string.Empty;
            InformationManager.ShowTextInquiry(
                new TextInquiryData(
                    "搜索可发送物品",
                    "输入关键词后确定，留空显示全部。",
                    true,
                    true,
                    "确定",
                    "取消",
                    input =>
                    {
                        ApplyMarketSearchKeyword(input);
                        StatusText = "已应用搜索关键词。";
                    },
                    () => { },
                    false,
                    null,
                    string.Empty,
                    defaultInput
                ),
                true,
                true
            );
        }

        public void ExecuteCloseMarketSearchPopup()
        {
            IsMarketSearchPopupVisible = false;
        }

        public void ExecuteApplyMarketSearchPopup()
        {
            ApplyMarketSearchKeyword(MarketSearchPopupText);
        }

        public void ExecuteClearMarketSearch()
        {
            CalradiaTavernDebug.Trace("VM", "ExecuteClearMarketSearch");
            if (_marketSearchKeyword.Length == 0)
            {
                return;
            }

            _marketSearchKeyword = string.Empty;
            MarketSearchPopupText = string.Empty;
            _marketPageIndex = 0;
            OnPropertyChanged(nameof(MarketSearchText));
            OnPropertyChanged(nameof(MarketSearchCurrentText));
            RefreshTradeInventory();
        }

        public void ExecuteApplyMarketSearch()
        {
            _marketPageIndex = 0;
            RefreshTradeInventory();
        }

        public void ExecutePrevMarketPage()
        {
            if (!CanPrevMarketPage)
            {
                return;
            }

            _marketPageIndex--;
            RefreshTradeInventory();
        }

        public void ExecuteNextMarketPage()
        {
            if (!CanNextMarketPage)
            {
                return;
            }

            _marketPageIndex++;
            RefreshTradeInventory();
        }

        public void ExecutePrevOnlinePlayersPage()
        {
            if (!CanPrevOnlinePlayersPage)
            {
                return;
            }

            _onlinePlayersPageIndex--;
            RefreshOnlinePlayers();
        }

        public void ExecuteNextOnlinePlayersPage()
        {
            if (!CanNextOnlinePlayersPage)
            {
                return;
            }

            _onlinePlayersPageIndex++;
            RefreshOnlinePlayers();
        }

        public void ExecutePrevTradePlayersPage()
        {
            if (!CanPrevTradePlayersPage)
            {
                return;
            }

            _tradePlayersPageIndex--;
            RefreshTradePlayers();
        }

        public void ExecuteNextTradePlayersPage()
        {
            if (!CanNextTradePlayersPage)
            {
                return;
            }

            _tradePlayersPageIndex++;
            RefreshTradePlayers();
        }

        public void ExecuteMarketPrimaryAction(string listingId)
        {
            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            string normalized = (listingId ?? string.Empty).Trim();
            CalradiaTavernDebug.Trace("VM", "ExecuteMarketPrimaryAction id=" + normalized);
            if (normalized.Length == 0)
            {
                return;
            }

            if (TryResolveTargetPlayerName(normalized, out string targetPlayerName))
            {
                SelectTargetPlayer(targetPlayerName);
                return;
            }

            if (normalized.StartsWith("item:", StringComparison.Ordinal))
            {
                string itemId = normalized.Substring("item:".Length).Trim();
                if (itemId.Length == 0)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(_selectedTargetPlayerName))
                {
                    StatusText = "请先选择目标玩家。";
                    return;
                }

                PublishItemIdText = itemId;
                PublishSelectedItemText = ResolvePublishItemDisplayText(itemId);
                PublishPriceText = "1";
                IsPublishSelectorPopupVisible = false;
                IsPublishPricePopupVisible = true;
                StatusText = "已选择物品，输入数量后确认发送。";
                CalradiaTavernDebug.Trace("VM", "OpenSendCountPopup item=" + itemId);
                return;
            }

            if (_inventoryById.ContainsKey(normalized))
            {
                if (string.IsNullOrWhiteSpace(_selectedTargetPlayerName))
                {
                    StatusText = "请先选择目标玩家。";
                    return;
                }

                PublishItemIdText = normalized;
                PublishSelectedItemText = ResolvePublishItemDisplayText(normalized);
                PublishPriceText = "1";
                IsPublishSelectorPopupVisible = false;
                IsPublishPricePopupVisible = true;
                StatusText = "已选择物品，输入数量后确认发送。";
                CalradiaTavernDebug.Trace("VM", "OpenSendCountPopup item=" + normalized);
                return;
            }

            if (_marketPrimaryActionById.TryGetValue(normalized, out TavernMarketActionKind kind)
                && kind == TavernMarketActionKind.Cancel
                && TryResolveTargetPlayerName(normalized, out string fallbackTarget))
            {
                SelectTargetPlayer(fallbackTarget);
                return;
            }

            StatusText = "未识别该行操作。";
            CalradiaTavernDebug.Trace("VM", "ExecuteMarketPrimaryAction unresolved id=" + normalized);
        }

        public void ExecuteSelectTargetPlayer(string playerToken)
        {
            if (_behavior == null)
            {
                StatusText = "行为实例未就绪。";
                return;
            }

            string token = (playerToken ?? string.Empty).Trim();
            CalradiaTavernDebug.Trace("VM", "ExecuteSelectTargetPlayer token=" + token);
            if (token.Length == 0)
            {
                return;
            }

            if (_playerNameBySelectToken.TryGetValue(token, out string mappedName) && !string.IsNullOrWhiteSpace(mappedName))
            {
                CalradiaTavernDebug.Trace("VM", "ExecuteSelectTargetPlayer mapped token=" + token + " -> " + mappedName);
                SelectTargetPlayer(mappedName);
                return;
            }

            if (TryResolveTargetPlayerName(token, out string resolvedName))
            {
                CalradiaTavernDebug.Trace("VM", "ExecuteSelectTargetPlayer resolved token=" + token + " -> " + resolvedName);
                SelectTargetPlayer(resolvedName);
                return;
            }

            string normalized = NormalizePlayerSelectionToken(token);
            if (normalized.Length > 0)
            {
                SelectTargetPlayer(normalized);
                return;
            }

            CalradiaTavernDebug.Trace("VM", "ExecuteSelectTargetPlayer unresolved token=" + token);
        }

        public void ExecuteClose()
        {
            _onClose?.Invoke();
        }

        private void RefreshAllData(bool forceInventory)
        {
            RefreshChatLines();
            RefreshOnlinePlayers();
            RefreshMarketData(forceInventory);
        }

        private void RefreshMarketData(bool refreshInventory)
        {
            RefreshTradePlayers();
            if (refreshInventory)
            {
                RefreshTradeInventory();
            }

            if (refreshInventory || IsPublishSelectorPopupVisible)
            {
                RefreshPublishableItems();
            }
        }

        private void RefreshChatLines()
        {
            if (_behavior == null)
            {
                return;
            }

            IReadOnlyList<TavernChatLine> lines = _behavior.GetRecentChatLines(220);
            List<TavernChatLine> source = lines.Where(x => x != null).ToList();

            // Force ascending timeline for UI: older at top, newer at bottom.
            bool hasAnyTimestamp = false;
            long sourceFirstPositiveMs = 0L;
            long sourceLastPositiveMs = 0L;
            for (int i = 0; i < source.Count; i++)
            {
                long sourceMs = NormalizeUnixTimeMs(source[i]?.UnixTimeMs ?? 0L);
                if (sourceMs > 0L)
                {
                    hasAnyTimestamp = true;
                    if (sourceFirstPositiveMs <= 0L)
                    {
                        sourceFirstPositiveMs = sourceMs;
                    }

                    sourceLastPositiveMs = sourceMs;
                }
            }

            List<TavernChatLine> ordered;
            if (hasAnyTimestamp)
            {
                bool sourceLooksDescending =
                    sourceFirstPositiveMs > 0L
                    && sourceLastPositiveMs > 0L
                    && sourceFirstPositiveMs > sourceLastPositiveMs;

                ordered = source
                    .Select(
                        (line, index) =>
                            new
                            {
                                Line = line,
                                Time = NormalizeUnixTimeMs(line?.UnixTimeMs ?? 0L),
                                TieIndex = sourceLooksDescending ? (source.Count - index) : index,
                                MessageId = line?.MessageId ?? string.Empty,
                            }
                    )
                    .OrderBy(x => x.Time <= 0L ? long.MaxValue : x.Time)
                    .ThenBy(x => x.TieIndex)
                    .ThenBy(x => x.MessageId, StringComparer.Ordinal)
                    .Select(x => x.Line)
                    .ToList();
            }
            else
            {
                ordered = source;
            }

            if (ordered.Count > 140)
            {
                ordered = ordered.Skip(ordered.Count - 140).ToList();
            }

            bool nonMonotonicOrder = false;
            long firstUnixMs = 0;
            long lastUnixMs = 0;
            long prevUnixMs = 0;
            string firstId = string.Empty;
            string lastId = string.Empty;
            for (int i = 0; i < ordered.Count; i++)
            {
                TavernChatLine sourceLine = ordered[i];
                if (sourceLine == null)
                {
                    continue;
                }

                long currentUnixMs = NormalizeUnixTimeMs(sourceLine.UnixTimeMs);
                if (currentUnixMs <= 0)
                {
                    currentUnixMs = 0;
                }

                if (firstUnixMs == 0)
                {
                    firstUnixMs = currentUnixMs;
                    firstId = sourceLine.MessageId ?? string.Empty;
                }
                else if (currentUnixMs < prevUnixMs)
                {
                    nonMonotonicOrder = true;
                }

                prevUnixMs = currentUnixMs;
                lastUnixMs = currentUnixMs;
                lastId = sourceLine.MessageId ?? string.Empty;
            }

            List<TavernChatLineVM> desired = new List<TavernChatLineVM>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                TavernChatLine line = ordered[i];
                string sender = string.IsNullOrWhiteSpace(line.PlayerName)
                    ? (line.IsSelf ? (_behavior.DisplayName ?? "Me") : "Anonymous")
                    : line.PlayerName.Trim();
                long unixTimeMs = NormalizeUnixTimeMs(line.UnixTimeMs);
                if (unixTimeMs <= 0)
                {
                    unixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                string text = CalradiaTavernCampaignBehavior.FormatChatToast(
                    sender,
                    line.Text ?? string.Empty,
                    unixTimeMs
                );
                desired.Add(new TavernChatLineVM(line.MessageId ?? string.Empty, text, line.IsSelf));
            }

            string firstVisualLine = desired.Count > 0 ? ClipText(desired[0].LineText, 48) : string.Empty;
            string lastVisualLine = desired.Count > 0
                ? ClipText(desired[desired.Count - 1].LineText, 48)
                : string.Empty;

            bool same = ChatLines.Count == desired.Count;
            if (same)
            {
                for (int i = 0; i < desired.Count; i++)
                {
                    TavernChatLineVM current = ChatLines[i];
                    TavernChatLineVM next = desired[i];
                    if (
                        !string.Equals(current.MessageId, next.MessageId, StringComparison.Ordinal)
                        || !string.Equals(current.LineText, next.LineText, StringComparison.Ordinal)
                        || current.IsSelf != next.IsSelf
                    )
                    {
                        same = false;
                        break;
                    }
                }
            }

            if (same)
            {
                long nowMs = CalradiaTavernDebug.NowMs;
                if (nonMonotonicOrder && nowMs >= _nextChatOrderDiagLogMs)
                {
                    _nextChatOrderDiagLogMs = nowMs + 1000L;
                    CalradiaTavernDebug.Trace(
                        "VM",
                        "RefreshChatLines unchanged but source non-monotonic count="
                            + source.Count
                            + " firstId="
                            + firstId
                            + " firstMs="
                            + firstUnixMs
                            + " lastId="
                            + lastId
                            + " lastMs="
                            + lastUnixMs
                    );
                }
                return;
            }

            ChatLines.Clear();
            for (int i = 0; i < desired.Count; i++)
            {
                ChatLines.Add(desired[i]);
            }

            _chatVisualVersion++;
            OnPropertyChanged(nameof(ChatVisualVersion));
            CalradiaTavernDebug.Trace(
                "VM",
                "RefreshChatLines rebuilt count="
                    + desired.Count
                    + " sourceFirstId="
                    + firstId
                    + " sourceFirstMs="
                    + firstUnixMs
                    + " sourceLastId="
                    + lastId
                    + " sourceLastMs="
                    + lastUnixMs
                    + " sourceNonMonotonic="
                    + nonMonotonicOrder
                    + " firstVisual="
                    + firstVisualLine
                    + " lastVisual="
                    + lastVisualLine
                    + " visualVersion="
                    + _chatVisualVersion
            );
        }

        private void RefreshOnlinePlayers()
        {
            if (_behavior == null)
            {
                return;
            }

            IReadOnlyList<string> players = _behavior.GetKnownPlayers(200);
            OnlinePlayers.Clear();
            string selfName = (_behavior.DisplayName ?? string.Empty).Trim();
            _allOnlinePlayerNames.Clear();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < players.Count; i++)
            {
                string name = (players[i] ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(name))
                {
                    continue;
                }

                _allOnlinePlayerNames.Add(name);
            }

            if (selfName.Length > 0 && !seen.Contains(selfName))
            {
                _allOnlinePlayerNames.Insert(0, selfName);
            }

            int totalCount = _allOnlinePlayerNames.Count;
            _onlinePlayersTotalPages = Math.Max(1, (totalCount + PlayerListPageSize - 1) / PlayerListPageSize);
            if (_onlinePlayersPageIndex >= _onlinePlayersTotalPages)
            {
                _onlinePlayersPageIndex = _onlinePlayersTotalPages - 1;
            }
            if (_onlinePlayersPageIndex < 0)
            {
                _onlinePlayersPageIndex = 0;
            }

            int skip = _onlinePlayersPageIndex * PlayerListPageSize;
            IEnumerable<string> pageNames = _allOnlinePlayerNames.Skip(skip).Take(PlayerListPageSize);
            foreach (string name in pageNames)
            {
                bool canBlock = !string.Equals(name, selfName, StringComparison.OrdinalIgnoreCase);
                OnlinePlayers.Add(
                    new TavernOnlinePlayerVM(
                        canBlock ? name : (name + "（你）"),
                        canBlock,
                        row => ExecuteBlockPlayer(row?.NameText)
                    )
                );
            }

            OnPropertyChanged(nameof(OnlinePlayersPageText));
            OnPropertyChanged(nameof(CanPrevOnlinePlayersPage));
            OnPropertyChanged(nameof(CanNextOnlinePlayersPage));
        }

        private void RefreshTradePlayers()
        {
            if (_behavior == null)
            {
                return;
            }

            IReadOnlyList<string> players = _behavior.GetKnownPlayers(200);
            string selected = _selectedTargetPlayerName;

            _allTradeTargetPlayerNames.Clear();
            _allTradeTargetPlayerNames.Add(LocalTradeBotName);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LocalTradeBotName };
            MyMarketListings.Clear();
            _marketPrimaryActionById.Clear();
            _playerNameBySelectToken.Clear();

            for (int i = 0; i < players.Count; i++)
            {
                string name = (players[i] ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }
                if (string.Equals(name, "系统", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (string.Equals(name, "system", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (string.Equals(name, LocalTradeBotName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!seen.Add(name))
                {
                    continue;
                }

                _allTradeTargetPlayerNames.Add(name);
            }

            int totalCount = _allTradeTargetPlayerNames.Count;
            _tradePlayersTotalPages = Math.Max(1, (totalCount + PlayerListPageSize - 1) / PlayerListPageSize);
            if (_tradePlayersPageIndex >= _tradePlayersTotalPages)
            {
                _tradePlayersPageIndex = _tradePlayersTotalPages - 1;
            }
            if (_tradePlayersPageIndex < 0)
            {
                _tradePlayersPageIndex = 0;
            }

            int skip = _tradePlayersPageIndex * PlayerListPageSize;
            IEnumerable<string> pageNames = _allTradeTargetPlayerNames.Skip(skip).Take(PlayerListPageSize);
            foreach (string name in pageNames)
            {
                AddTradeTargetRow(name, selected);
            }

            OnPropertyChanged(nameof(MyListingsLimitText));
            OnPropertyChanged(nameof(MarketSearchCurrentText));
            OnPropertyChanged(nameof(TradePlayersPageText));
            OnPropertyChanged(nameof(CanPrevTradePlayersPage));
            OnPropertyChanged(nameof(CanNextTradePlayersPage));

            CalradiaTavernDebug.Trace(
                "VM",
                "RefreshTradePlayers rows="
                    + MyMarketListings.Count.ToString(CultureInfo.InvariantCulture)
                    + " page="
                    + (_tradePlayersPageIndex + 1).ToString(CultureInfo.InvariantCulture)
                    + "/"
                    + _tradePlayersTotalPages.ToString(CultureInfo.InvariantCulture)
                    + " selected="
                    + _selectedTargetPlayerName
            );
        }

        private void AddTradeTargetRow(string name, string selected)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string normalizedName = name.Trim();
            bool isSelected = string.Equals(normalizedName, selected, StringComparison.OrdinalIgnoreCase);
            string rowId = "player:" + normalizedName;
            string actionText = isSelected ? "已选" : "选择";
            string displayName = isSelected ? normalizedName + "  (目标)" : normalizedName;

            MyMarketListings.Add(
                new TavernMarketListingVM(
                    rowId,
                    displayName,
                    string.Empty,
                    "在线",
                    string.Empty,
                    string.Empty,
                    actionText,
                    true,
                    TavernMarketActionKind.Cancel,
                    row =>
                    {
                        CalradiaTavernDebug.Trace("VM", "PlayerRowClick id=" + (row?.ListingId ?? string.Empty));
                        ExecuteSelectTargetPlayer(row?.ListingId);
                    }
                )
            );
            _marketPrimaryActionById[rowId] = TavernMarketActionKind.Cancel;
            _marketPrimaryActionById[normalizedName] = TavernMarketActionKind.Cancel;
            _marketPrimaryActionById[displayName] = TavernMarketActionKind.Cancel;
            _playerNameBySelectToken[rowId] = normalizedName;
            _playerNameBySelectToken[normalizedName] = normalizedName;
            _playerNameBySelectToken[displayName] = normalizedName;
        }

        private void RefreshTradeInventory()
        {
            if (_behavior == null)
            {
                return;
            }

            MarketListings.Clear();

            string keyword = (_marketSearchKeyword ?? string.Empty).Trim();
            List<TavernInventoryEntry> ordered = _behavior.GetDirectTradeInventoryEntries(keyword, 180);

            _inventoryById.Clear();
            for (int i = 0; i < ordered.Count; i++)
            {
                TavernInventoryEntry entry = ordered[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.ItemId))
                {
                    continue;
                }

                _inventoryById[entry.ItemId.Trim()] = entry;
            }

            int totalCount = ordered.Count;
            _marketFilteredCount = totalCount;
            _marketVisibleCount = 0;
            _marketTotalPages = Math.Max(1, (totalCount + TradePageSize - 1) / TradePageSize);
            if (_marketPageIndex >= _marketTotalPages)
            {
                _marketPageIndex = _marketTotalPages - 1;
            }
            if (_marketPageIndex < 0)
            {
                _marketPageIndex = 0;
            }

            int skip = _marketPageIndex * TradePageSize;
            IEnumerable<TavernInventoryEntry> pageItems = ordered.Skip(skip).Take(TradePageSize);

            bool hasTarget = !string.IsNullOrWhiteSpace(_selectedTargetPlayerName);
            string targetText = hasTarget ? ClipText(_selectedTargetPlayerName, 14) : "未选择";

            foreach (TavernInventoryEntry entry in pageItems)
            {
                string itemId = (entry.ItemId ?? string.Empty).Trim();
                if (itemId.Length == 0)
                {
                    continue;
                }

                string rowId = "item:" + itemId;
                string actionText = hasTarget ? "发送" : "先选玩家";
                string stateText = hasTarget ? "可发送" : "等待目标";
                string itemName = ClipText(string.IsNullOrWhiteSpace(entry.Name) ? itemId : entry.Name.Trim(), 20);
                string shortItemId = ClipText(itemId, 14);
                string quantityText = "x" + Math.Max(0, entry.Count).ToString(CultureInfo.InvariantCulture);

                MarketListings.Add(
                    new TavernMarketListingVM(
                        rowId,
                        itemName,
                        targetText,
                        quantityText,
                        stateText,
                        shortItemId,
                        actionText,
                        hasTarget,
                        TavernMarketActionKind.Buy,
                        row =>
                        {
                            ExecuteMarketPrimaryAction(row?.ListingId);
                        }
                    )
                );
                _marketPrimaryActionById[rowId] = TavernMarketActionKind.Buy;
                _marketPrimaryActionById[itemId] = TavernMarketActionKind.Buy;
                _marketVisibleCount++;
            }

            OnPropertyChanged(nameof(MarketPageText));
            OnPropertyChanged(nameof(CanPrevMarketPage));
            OnPropertyChanged(nameof(CanNextMarketPage));
            OnPropertyChanged(nameof(MarketSearchCurrentText));
            OnPropertyChanged(nameof(MarketResultSummaryText));
        }

        private void RefreshPublishableItems()
        {
            if (_behavior == null)
            {
                return;
            }

            string keyword = (PublishSearchText ?? string.Empty).Trim();
            List<TavernInventoryEntry> entries = _behavior.GetDirectTradeInventoryEntries(keyword, 180);

            PublishableItems.Clear();
            for (int i = 0; i < entries.Count; i++)
            {
                TavernInventoryEntry entry = entries[i];
                if (entry == null || entry.Count <= 0 || string.IsNullOrWhiteSpace(entry.ItemId))
                {
                    continue;
                }

                string line = ClipText((entry.Name ?? entry.ItemId), 24)
                    + " ("
                    + ClipText(entry.ItemId, 20)
                    + ") x"
                    + Math.Max(0, entry.Count).ToString(CultureInfo.InvariantCulture);

                PublishableItems.Add(
                    new TavernMarketOwnedItemVM(
                        entry.ItemId,
                        line,
                        row => ExecuteSelectPublishItem(row?.ItemId)
                    )
                );
                _inventoryById[entry.ItemId.Trim()] = entry;
            }

            OnPropertyChanged(nameof(HasPublishableItems));
        }

        private string ResolvePublishItemDisplayText(string itemId)
        {
            string normalized = (itemId ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            if (_inventoryById.TryGetValue(normalized, out TavernInventoryEntry entry) && entry != null)
            {
                string name = string.IsNullOrWhiteSpace(entry.Name) ? normalized : entry.Name.Trim();
                string target = string.IsNullOrWhiteSpace(_selectedTargetPlayerName)
                    ? "未选择目标"
                    : _selectedTargetPlayerName;
                return name + " (" + normalized + ") -> " + target;
            }

            return normalized;
        }

        private void ApplyMarketSearchKeyword(string input)
        {
            IsMarketSearchPopupVisible = false;
            string next = (input ?? string.Empty).Trim();
            MarketSearchPopupText = next;
            if (string.Equals(next, _marketSearchKeyword, StringComparison.Ordinal))
            {
                return;
            }

            _marketSearchKeyword = next;
            _marketPageIndex = 0;
            OnPropertyChanged(nameof(MarketSearchText));
            OnPropertyChanged(nameof(MarketSearchCurrentText));
            RefreshTradeInventory();
        }

        private static string ClipText(string text, int maxLength)
        {
            string value = (text ?? string.Empty).Trim();
            if (maxLength <= 0 || value.Length <= maxLength)
            {
                return value;
            }

            int keep = Math.Max(1, maxLength - 3);
            return value.Substring(0, Math.Min(value.Length, keep)) + "...";
        }
        private void SelectTargetPlayer(string playerName)
        {
            string next = NormalizePlayerSelectionToken(playerName);
            if (next.Length == 0)
            {
                return;
            }

            _selectedTargetPlayerName = next;
            if (TryFindTradeTargetPlayerPageIndex(next, out int pageIndex))
            {
                _tradePlayersPageIndex = pageIndex;
            }
            StatusText = "已选择目标玩家：" + next;
            OnPropertyChanged(nameof(MyListingsLimitText));
            OnPropertyChanged(nameof(MarketSearchCurrentText));
            RefreshTradePlayers();
            RefreshTradeInventory();
        }

        private bool TryResolveTargetPlayerName(string rawToken, out string playerName)
        {
            playerName = string.Empty;
            string token = (rawToken ?? string.Empty).Trim();
            string normalized = NormalizePlayerSelectionToken(token);
            if (normalized.Length == 0)
            {
                return false;
            }

            if (normalized.StartsWith("item:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (TryMatchKnownPlayer(normalized, out playerName))
            {
                return true;
            }

            for (int i = 0; i < MyMarketListings.Count; i++)
            {
                TavernMarketListingVM row = MyMarketListings[i];
                if (row == null)
                {
                    continue;
                }

                bool sameId = string.Equals(
                    (row.ListingId ?? string.Empty).Trim(),
                    token,
                    StringComparison.OrdinalIgnoreCase
                );
                bool sameText = string.Equals(
                    (row.ItemText ?? string.Empty).Trim(),
                    token,
                    StringComparison.OrdinalIgnoreCase
                );
                if (!sameId && !sameText)
                {
                    continue;
                }

                if (TryMatchKnownPlayer(row.ListingId, out playerName))
                {
                    return true;
                }

                if (TryMatchKnownPlayer(row.ItemText, out playerName))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryFindTradeTargetPlayerPageIndex(string playerName, out int pageIndex)
        {
            pageIndex = 0;
            string normalized = NormalizePlayerSelectionToken(playerName);
            if (normalized.Length == 0)
            {
                return false;
            }

            int index = -1;
            for (int i = 0; i < _allTradeTargetPlayerNames.Count; i++)
            {
                if (
                    string.Equals(
                        NormalizePlayerSelectionToken(_allTradeTargetPlayerNames[i]),
                        normalized,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                IReadOnlyList<string> players = _behavior?.GetKnownPlayers(200) ?? Array.Empty<string>();
                List<string> all = new List<string> { LocalTradeBotName };
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { LocalTradeBotName };
                for (int i = 0; i < players.Count; i++)
                {
                    string current = (players[i] ?? string.Empty).Trim();
                    if (current.Length == 0)
                    {
                        continue;
                    }
                    if (string.Equals(current, "系统", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (string.Equals(current, "system", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!seen.Add(current))
                    {
                        continue;
                    }

                    all.Add(current);
                }

                for (int i = 0; i < all.Count; i++)
                {
                    if (
                        string.Equals(
                            NormalizePlayerSelectionToken(all[i]),
                            normalized,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index < 0)
            {
                return false;
            }

            pageIndex = index / PlayerListPageSize;
            return true;
        }

        private bool TryMatchKnownPlayer(string candidate, out string matchedPlayerName)
        {
            matchedPlayerName = string.Empty;
            string normalized = NormalizePlayerSelectionToken(candidate);
            if (normalized.Length == 0)
            {
                return false;
            }

            if (_behavior == null)
            {
                matchedPlayerName = normalized;
                return true;
            }

            IReadOnlyList<string> players = _behavior.GetKnownPlayers(200);
            for (int i = 0; i < players.Count; i++)
            {
                string current = NormalizePlayerSelectionToken(players[i]);
                if (current.Length == 0)
                {
                    continue;
                }

                if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    matchedPlayerName = (players[i] ?? string.Empty).Trim();
                    return true;
                }
            }

            return false;
        }

        private static string NormalizePlayerSelectionToken(string token)
        {
            string value = (token ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            if (value.StartsWith("player:", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("player:".Length).Trim();
            }

            int asciiOpen = value.LastIndexOf('(');
            if (asciiOpen > 0 && value.EndsWith(")", StringComparison.Ordinal))
            {
                value = value.Substring(0, asciiOpen).Trim();
            }

            int cjkOpen = value.LastIndexOf('（');
            if (cjkOpen > 0 && value.EndsWith("）", StringComparison.Ordinal))
            {
                value = value.Substring(0, cjkOpen).Trim();
            }

            return value;
        }
        private static bool IsTradeAccepted(string resultText)
        {
            if (string.IsNullOrWhiteSpace(resultText))
            {
                return false;
            }

            return resultText.StartsWith("Sending in background", StringComparison.OrdinalIgnoreCase);
        }

        private static long NormalizeUnixTimeMs(long value)
        {
            if (value <= 0)
            {
                return 0;
            }

            if (value < 100_000_000_000L)
            {
                return value * 1000L;
            }

            if (value > 100_000_000_000_000L)
            {
                return value / 1000L;
            }

            return value;
        }
    }
}



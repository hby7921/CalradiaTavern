using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using CalradiaTavern.Models;
using CalradiaTavern.Networking;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace CalradiaTavern.Behaviors
{
    public sealed class TavernChatLine
    {
        public string MessageId { get; set; }
        public string PlayerName { get; set; }
        public string Text { get; set; }
        public long UnixTimeMs { get; set; }
        public bool IsSelf { get; set; }
    }

    public sealed class TavernInventoryEntry
    {
        public string ItemId { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
    }

    public enum TavernMarketCategory
    {
        All = 0,
        Melee = 1,
        ShieldRanged = 2,
        Armor = 3,
        Banner = 4,
    }

    public sealed class TavernMarketListing
    {
        public string ListingId { get; set; }
        public string SellerPlayerId { get; set; }
        public string SellerName { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public int ItemCount { get; set; }
        public int PriceDenars { get; set; }
        public TavernMarketCategory Category { get; set; }
        public string Status { get; set; }
        public long CreatedUnixTimeMs { get; set; }
        public long PublicUnixTimeMs { get; set; }
        public long UpdatedUnixTimeMs { get; set; }
        public string BuyerPlayerId { get; set; }
        public string BuyerName { get; set; }

        public bool IsOpen()
        {
            return string.Equals(Status ?? string.Empty, "open", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsPublic(long nowUnixMs)
        {
            return nowUnixMs >= Math.Max(0L, PublicUnixTimeMs);
        }
    }

    public sealed class CalradiaTavernCampaignBehavior : CampaignBehaviorBase
    {
        private sealed class PendingLocalChatEntry
        {
            public string LocalMessageId { get; set; }
            public string Text { get; set; }
            public long CreatedUnixTimeMs { get; set; }
        }

        private const int MaxChatLength = 180;
        private const int MaxSeenIdCache = 1000;
        private const int MaxChatCache = 180;
        private const int MaxOnlinePlayerCache = 200;
        public const int MarketMaxListingsPerPlayer = 5;
        public const int MarketPublicitySeconds = 300;
        public const int MarketListingLifetimeSeconds = 2 * 24 * 60 * 60;
        private const int MaxMarketListingsCache = 300;
        private const int MaxMarketPrice = 2_000_000;
        private const int MaxDirectTradeInventoryEntries = 240;
        private const string LocalTradeBotName = "交易机器人";
        private const string SystemPseudoPlayerName = "系统";
        private const int ChatCooldownDefaultSeconds = 10;
        private const string TargetBlockedYouMessage = "该玩家已经拉黑你，无法赠送。";
        private const string TargetNotOnMapMessage = "该玩家不在大地图，物品无法送达";
        private static readonly string[] LocalTradeBotAliases =
        {
            "交易机器人",
            "trade bot",
            "tradebot",
            "bot",
        };
        private const string DenarDeliveryItemId = "__ctavern_denar__";
        private const float PollIntervalSeconds = 1.2f;
        private const float RegisterIntervalSeconds = 60f;
        private const int OnlinePlayerActiveWindowSec = 120;
        private static bool ShowChatToastInNativeFeed => true;
        private const string FixedChannelId = "global";
        private const string DefaultServerUrl = CalradiaTavernSettings.BuiltInServerUrl;

        private string _playerId = string.Empty;
        private string _displayName = string.Empty;
        private string _channelId = FixedChannelId;
        private string _serverUrl = DefaultServerUrl;
        private long _lastChatUnixMs;
        private long _lastDeliveryUnixMs;
        private List<string> _seenChatIds = new List<string>();
        private List<string> _seenDeliveryIds = new List<string>();
        private List<TavernChatLine> _chatLines = new List<TavernChatLine>();
        private List<string> _onlinePlayerNames = new List<string>();
        private List<TavernMarketListing> _marketListings = new List<TavernMarketListing>();
        private Dictionary<string, long> _onlinePlayerLastSeenUnixMs =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private int _unreadChatCount;
        private float _pollElapsed;
        private float _registerElapsed;
        private TavernApiClient _api;
        private bool _sessionCursorInitialized;
        private readonly object _netLock = new object();
        private readonly object _mainThreadQueueLock = new object();
        private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
        private readonly Dictionary<string, PendingLocalChatEntry> _pendingLocalChatsByNonce =
            new Dictionary<string, PendingLocalChatEntry>(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingGiftRequestIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _blockedPlayerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<string> _blockedPlayerNamesSync = new List<string>();
        private long _lastChatSendUnixMs;
        private int _pullInFlight;
        private int _upsertInFlight;
        private long _lastPullPlayersErrorLogMs;
        private long _nextChatOrderDiagLogMs;
        private bool _giftRequestsEndpointUnavailable;
        private bool _blockedPlayersEndpointUnavailable;
        private readonly object _nativeItemIdsLock = new object();
        private HashSet<string> _nativeItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _nativeItemIdsLoaded;
        private bool _presenceActive;

        public static event Action StateChanged;

        public static CalradiaTavernCampaignBehavior Instance =>
            Campaign.Current?.GetCampaignBehavior<CalradiaTavernCampaignBehavior>();

        public int UnreadChatCount => Math.Max(0, _unreadChatCount);

        public string DisplayName => _displayName;

        public string PlayerId => _playerId;

        public override void RegisterEvents()
        {
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("CT_PlayerId", ref _playerId);
            dataStore.SyncData("CT_DisplayName", ref _displayName);
            dataStore.SyncData("CT_ChannelId", ref _channelId);
            dataStore.SyncData("CT_ServerUrl", ref _serverUrl);
            dataStore.SyncData("CT_LastChatUnixMs", ref _lastChatUnixMs);
            dataStore.SyncData("CT_LastDeliveryUnixMs", ref _lastDeliveryUnixMs);
            dataStore.SyncData("CT_SeenChatIds", ref _seenChatIds);
            dataStore.SyncData("CT_SeenDeliveryIds", ref _seenDeliveryIds);
            dataStore.SyncData("CT_UnreadChatCount", ref _unreadChatCount);
            dataStore.SyncData("CT_BlockedPlayerNames", ref _blockedPlayerNamesSync);
            dataStore.SyncData("CT_LastChatSendUnixMs", ref _lastChatSendUnixMs);

            _playerId ??= string.Empty;
            _displayName ??= string.Empty;
            _channelId = FixedChannelId;
            _serverUrl = NormalizeServerUrl(_serverUrl) ?? DefaultServerUrl;
            _lastChatUnixMs = NormalizeUnixTimeMs(_lastChatUnixMs);
            _lastDeliveryUnixMs = NormalizeUnixTimeMs(_lastDeliveryUnixMs);
            _seenChatIds ??= new List<string>();
            _seenDeliveryIds ??= new List<string>();
            _chatLines ??= new List<TavernChatLine>();
            _onlinePlayerNames ??= new List<string>();
            _marketListings ??= new List<TavernMarketListing>();
            _blockedPlayerNamesSync ??= new List<string>();
            _onlinePlayerLastSeenUnixMs ??= new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase
            );
            _blockedPlayerNamesSync ??= new List<string>();
            _blockedPlayerNames.Clear();
            for (int i = 0; i < _blockedPlayerNamesSync.Count; i++)
            {
                string current = (_blockedPlayerNamesSync[i] ?? string.Empty).Trim();
                if (current.Length > 0)
                {
                    _blockedPlayerNames.Add(current);
                }
            }
            _unreadChatCount = Math.Max(0, _unreadChatCount);
        }

        public string PullNow()
        {
            DrainMainThreadQueue();
            EnsureReady();
            if (_presenceActive)
            {
                TryUpsertPlayer(false);
            }
            QueueBackgroundPull();
            return "Refresh requested (running in background).";
        }

        public void SetPresenceActive(bool active)
        {
            DrainMainThreadQueue();
            EnsureReady();
            if (_presenceActive == active)
            {
                return;
            }

            _presenceActive = active;
            CalradiaTavernDebug.Trace("Behavior", "SetPresenceActive active=" + _presenceActive);
            if (_presenceActive)
            {
                TryUpsertPlayer(false);
                QueueBackgroundPull();
                return;
            }

            TryUpsertPlayer(false, true);

            if (_onlinePlayerNames.Count > 0 || _onlinePlayerLastSeenUnixMs.Count > 0)
            {
                _onlinePlayerNames.Clear();
                _onlinePlayerLastSeenUnixMs.Clear();
                NotifyStateChanged();
            }
        }

        public IReadOnlyList<TavernChatLine> GetRecentChatLines(int maxCount = 120)
        {
            DrainMainThreadQueue();
            EnsureReady();
            int take = Math.Max(1, Math.Min(300, maxCount));
            int skip = Math.Max(0, _chatLines.Count - take);
            return _chatLines.Skip(skip).Where(x => x != null).ToList();
        }
        public IReadOnlyList<string> GetKnownPlayers(int maxCount = 80)
        {
            DrainMainThreadQueue();
            EnsureReady();
            int take = Math.Max(1, Math.Min(200, maxCount));
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long floorMs = nowMs - (OnlinePlayerActiveWindowSec * 1000L);
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_onlinePlayerLastSeenUnixMs != null && _onlinePlayerLastSeenUnixMs.Count > 0)
            {
                foreach (
                    KeyValuePair<string, long> kv in _onlinePlayerLastSeenUnixMs
                        .Where(x => x.Value >= floorMs)
                        .OrderByDescending(x => x.Value)
                )
                {
                    if (names.Count >= take)
                    {
                        break;
                    }

                    string normalized = (kv.Key ?? string.Empty).Trim();
                    if (normalized.Length == 0)
                    {
                        continue;
                    }
                    if (IsReservedPseudoPlayerName(normalized))
                    {
                        continue;
                    }
                    if (!seen.Add(normalized))
                    {
                        continue;
                    }

                    names.Add(normalized);
                }
            }

            string selfName = NormalizeDisplayName(_displayName) ?? string.Empty;
            if (_presenceActive && selfName.Length > 0 && !seen.Contains(selfName))
            {
                names.Insert(0, selfName);
                if (names.Count > take)
                {
                    names = names.Take(take).ToList();
                }
            }
            return names;
        }

        public string BlockPlayer(string targetPlayerName)
        {
            DrainMainThreadQueue();
            EnsureReady();

            string target = NormalizeDisplayName(targetPlayerName) ?? string.Empty;
            if (target.Length == 0)
            {
                return "拉黑失败：玩家名为空。";
            }
            if (string.Equals(target, _displayName, StringComparison.OrdinalIgnoreCase))
            {
                return "不能拉黑自己。";
            }
            if (IsReservedPseudoPlayerName(target))
            {
                return "不能拉黑系统玩家。";
            }

            bool added = _blockedPlayerNames.Add(target);
            _blockedPlayerNamesSync = _blockedPlayerNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (!added)
            {
                return "该玩家已在黑名单中。";
            }

            TavernBlockPlayerRequest request = new TavernBlockPlayerRequest
            {
                ChannelId = _channelId,
                BlockerPlayerId = _playerId,
                BlockerPlayerName = _displayName,
                BlockedPlayerName = target,
                ClientNonce = Guid.NewGuid().ToString("N"),
            };
            string serverUrl = _serverUrl;

            Task.Run(
                () =>
                {
                    bool ok;
                    string error;
                    lock (_netLock)
                    {
                        EnsureApi();
                        _api.BaseUrl = serverUrl;
                        ok = _api.BlockPlayer(request, out TavernBlockPlayerResponse _, out error);
                    }

                    EnqueueMainThreadAction(
                        () =>
                        {
                            if (!ok)
                            {
                                CalradiaTavernDebug.Trace("Behavior", "BlockPlayer sync failed: " + error);
                            }
                        }
                    );
                }
            );

            NotifyStateChanged();
            return "已拉黑玩家：" + target;
        }

        public bool IsBlockedByMe(string playerName)
        {
            string normalized = NormalizeDisplayName(playerName) ?? string.Empty;
            return normalized.Length > 0 && _blockedPlayerNames.Contains(normalized);
        }

        public void ClearLocalChatCache()
        {
            DrainMainThreadQueue();
            EnsureReady();
            _chatLines.Clear();
            _pendingLocalChatsByNonce.Clear();
            _unreadChatCount = 0;
            NotifyStateChanged();
        }

        public void MarkChatRead()
        {
            DrainMainThreadQueue();
            if (_unreadChatCount <= 0)
            {
                return;
            }

            _unreadChatCount = 0;
            NotifyStateChanged();
        }

        public string SendChat(string rawText)
        {
            DrainMainThreadQueue();
            EnsureReady();

            long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // TEMP: disable chat cooldown to allow high-frequency local stress testing.

            string text = (rawText ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return "Message cannot be empty.";
            }

            if (text.Length > MaxChatLength)
            {
                text = text.Substring(0, MaxChatLength);
            }

            string selfName = string.IsNullOrWhiteSpace(_displayName) ? "Me" : _displayName;

            TavernSendChatRequest request = new TavernSendChatRequest
            {
                ChannelId = _channelId,
                PlayerId = _playerId,
                PlayerName = selfName,
                Text = text,
                ClientNonce = Guid.NewGuid().ToString("N"),
            };

            _lastChatSendUnixMs = nowUnixMs;
            long localUnixMs = RegisterPendingLocalChat(request.ClientNonce, selfName, text);
            if (ShowChatToastInNativeFeed)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage(FormatChatToast(selfName, text, localUnixMs), Colors.Cyan)
                );
            }
            QueueBackgroundSendChat(request, selfName, text);

            string preview = text.Length > 24 ? text.Substring(0, 24) + "..." : text;
            return selfName + " sending in background: " + preview;
        }

        public static string FormatChatToast(string playerName, string text, long unixTimeMs)
        {
            string sender = string.IsNullOrWhiteSpace(playerName) ? "Anonymous" : playerName.Trim();
            string body = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
            long normalizedMs = NormalizeUnixTimeMs(unixTimeMs);
            DateTimeOffset local = normalizedMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(normalizedMs).ToLocalTime()
                : DateTimeOffset.Now;
            return "[" + local.ToString("HH:mm", CultureInfo.InvariantCulture) + "] " + sender + ": " + body;
        }

        public List<TavernInventoryEntry> GetInventoryEntries()
        {
            EnsureReady();
            List<TavernInventoryEntry> result = new List<TavernInventoryEntry>();
            ItemRoster roster = MobileParty.MainParty?.ItemRoster;
            if (roster == null)
            {
                return result;
            }

            for (int i = 0; i < roster.Count; i++)
            {
                ItemObject item = roster.GetItemAtIndex(i);
                if (item == null)
                {
                    continue;
                }

                int count = Math.Max(0, roster.GetElementNumber(i));
                if (count <= 0)
                {
                    continue;
                }

                result.Add(
                    new TavernInventoryEntry
                    {
                        ItemId = item.StringId,
                        Name = item.Name?.ToString() ?? item.StringId,
                        Count = count,
                    }
                );
            }

            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }

        public List<TavernInventoryEntry> GetDirectTradeInventoryEntries(string keyword, int maxCount = 120)
        {
            EnsureReady();
            int take = Math.Max(1, Math.Min(MaxDirectTradeInventoryEntries, maxCount));
            string needle = (keyword ?? string.Empty).Trim();

            List<TavernInventoryEntry> result = new List<TavernInventoryEntry>();
            ItemRoster roster = MobileParty.MainParty?.ItemRoster;
            if (roster == null)
            {
                return result;
            }

            int skipInvalid = 0;
            int skipCount = 0;
            int skipKeyword = 0;
            List<string> invalidSamples = new List<string>();

            for (int i = 0; i < roster.Count; i++)
            {
                ItemObject item = roster.GetItemAtIndex(i);
                if (!TryValidateDirectTradeItem(item, out string invalidReason))
                {
                    skipInvalid++;
                    if (invalidSamples.Count < 12)
                    {
                        string id = item?.StringId ?? "<null>";
                        string type = item == null ? "null" : item.ItemType.ToString();
                        invalidSamples.Add(id + "[" + type + "]:" + invalidReason);
                    }
                    continue;
                }

                int count = Math.Max(0, roster.GetElementNumber(i));
                if (count <= 0)
                {
                    skipCount++;
                    continue;
                }

                string itemId = item.StringId ?? string.Empty;
                string itemName = item.Name?.ToString() ?? itemId;
                if (needle.Length > 0)
                {
                    if (
                        itemName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                        && itemId.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                    )
                    {
                        skipKeyword++;
                        continue;
                    }
                }

                result.Add(
                    new TavernInventoryEntry
                    {
                        ItemId = itemId,
                        Name = itemName,
                        Count = count,
                    }
                );
            }

            result = result
                .OrderByDescending(x => FindItem(x.ItemId)?.Value ?? 0)
                .ThenBy(x => x.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .ToList();

            if (needle.Length > 0 || result.Count <= 3)
            {
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "DirectTradeEntries keyword='"
                        + needle
                        + "' roster="
                        + roster.Count
                        + " matched="
                        + result.Count
                        + " skipInvalid="
                        + skipInvalid
                        + " skipCount="
                        + skipCount
                        + " skipKeyword="
                        + skipKeyword
                        + " invalidSamples="
                        + string.Join(" | ", invalidSamples)
                );
            }
            return result;
        }

        public IReadOnlyList<TavernMarketListing> GetMarketListings(int maxCount = 120)
        {
            DrainMainThreadQueue();
            EnsureReady();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int take = Math.Max(1, Math.Min(MaxMarketListingsCache, maxCount));
            return _marketListings
                .Where(x => x != null && x.IsOpen() && !IsMarketListingExpired(x, nowMs))
                .OrderByDescending(x => x.CreatedUnixTimeMs)
                .Take(take)
                .ToList();
        }

        public IReadOnlyList<TavernMarketListing> GetMyMarketListings(int maxCount = 30)
        {
            DrainMainThreadQueue();
            EnsureReady();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int take = Math.Max(1, Math.Min(MaxMarketListingsCache, maxCount));
            return _marketListings
                .Where(
                    x =>
                        x != null
                        && x.IsOpen()
                        && !IsMarketListingExpired(x, nowMs)
                        && string.Equals(x.SellerPlayerId, _playerId, StringComparison.Ordinal)
                )
                .OrderByDescending(x => x.CreatedUnixTimeMs)
                .Take(take)
                .ToList();
        }

        public List<TavernInventoryEntry> SearchPublishableInventoryEntries(string keyword, int maxCount = 50)
        {
            EnsureReady();
            int take = Math.Max(1, Math.Min(200, maxCount));
            string needle = (keyword ?? string.Empty).Trim();

            List<TavernInventoryEntry> all = GetInventoryEntries();
            List<TavernInventoryEntry> result = new List<TavernInventoryEntry>();

            foreach (TavernInventoryEntry entry in all)
            {
                if (entry == null || entry.Count <= 0 || string.IsNullOrWhiteSpace(entry.ItemId))
                {
                    continue;
                }

                ItemObject item = FindItem(entry.ItemId);
                if (!TryValidateMarketItem(item, out _, out _))
                {
                    continue;
                }

                if (needle.Length > 0)
                {
                    string name = entry.Name ?? string.Empty;
                    if (
                        name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                        && entry.ItemId.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0
                    )
                    {
                        continue;
                    }
                }

                result.Add(entry);
                if (result.Count >= take)
                {
                    break;
                }
            }

            return result;
        }

        public string PublishMarketListing(string itemId, int priceDenars)
        {
            DrainMainThreadQueue();
            EnsureReady();

            if (priceDenars <= 0 || priceDenars > MaxMarketPrice)
            {
                return "价格必须在 1 到 " + MaxMarketPrice + " 之间。";
            }

            if (GetMyMarketListings(MarketMaxListingsPerPlayer + 1).Count >= MarketMaxListingsPerPlayer)
            {
                return "每位玩家最多上架 " + MarketMaxListingsPerPlayer + " 件物品。";
            }

            ItemObject item = FindItem(itemId);
            if (!TryValidateMarketItem(item, out TavernMarketCategory category, out string validateError))
            {
                return "上架失败: " + validateError;
            }

            if (!TryTakeItem(item.StringId, 1, out ItemObject takenItem, out string takeError))
            {
                return "上架失败: " + takeError;
            }

            string serverUrl = _serverUrl;
            TavernMarketPublishRequest request = new TavernMarketPublishRequest
            {
                ChannelId = _channelId,
                SellerPlayerId = _playerId,
                SellerName = _displayName,
                ItemId = takenItem.StringId,
                ItemName = takenItem.Name?.ToString() ?? takenItem.StringId,
                ItemCount = 1,
                PriceDenars = priceDenars,
                Category = MarketCategoryToServerCode(category),
                ClientNonce = Guid.NewGuid().ToString("N"),
            };

            Task.Run(
                () =>
                {
                    bool ok = false;
                    TavernMarketPublishResponse response = null;
                    string error = string.Empty;
                    try
                    {
                        lock (_netLock)
                        {
                            EnsureApi();
                            _api.BaseUrl = serverUrl;
                            ok = _api.PublishMarketListing(request, out response, out error);
                        }
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = "Unexpected publish exception: " + ex.Message;
                    }

                    EnqueueMainThreadAction(
                        () =>
                        {
                            if (!ok)
                            {
                                GiveItem(request.ItemId, request.ItemCount);
                                InformationManager.DisplayMessage(
                                    new InformationMessage("[集市] 上架失败，已返还物品: " + error, Colors.Red)
                                );
                                return;
                            }

                            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            long publicMs = NormalizeUnixTimeMs(response?.PublicUnixTimeMs ?? 0L);
                            long waitSec = Math.Max(0L, (publicMs - nowMs + 999L) / 1000L);
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    "[集市] 已上架 "
                                        + request.ItemName
                                        + "，售价 "
                                        + request.PriceDenars
                                        + "，公示期约 "
                                        + waitSec
                                        + " 秒。",
                                    Colors.Green
                                )
                            );
                            QueueBackgroundPull();
                        }
                    );
                }
            );

            return "上架请求已发送。";
        }

        public string CancelMarketListing(string listingId)
        {
            DrainMainThreadQueue();
            EnsureReady();

            string normalized = (listingId ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "无效的上架记录。";
            }

            TavernMarketListing target = _marketListings.FirstOrDefault(
                x => string.Equals(x?.ListingId, normalized, StringComparison.Ordinal)
            );
            if (target == null)
            {
                return "未找到上架记录。";
            }

            if (!string.Equals(target.SellerPlayerId, _playerId, StringComparison.Ordinal))
            {
                return "只能下架自己的物品。";
            }

            string serverUrl = _serverUrl;
            TavernMarketCancelRequest request = new TavernMarketCancelRequest
            {
                ListingId = normalized,
                SellerPlayerId = _playerId,
                ClientNonce = Guid.NewGuid().ToString("N"),
            };

            Task.Run(
                () =>
                {
                    bool ok = false;
                    TavernMarketCancelResponse response = null;
                    string error = string.Empty;
                    try
                    {
                        lock (_netLock)
                        {
                            EnsureApi();
                            _api.BaseUrl = serverUrl;
                            ok = _api.CancelMarketListing(request, out response, out error);
                        }
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = "Unexpected cancel exception: " + ex.Message;
                    }

                    EnqueueMainThreadAction(
                        () =>
                        {
                            if (!ok)
                            {
                                InformationManager.DisplayMessage(
                                    new InformationMessage("[集市] 下架失败: " + error, Colors.Red)
                                );
                                return;
                            }

                            int returnCount = Math.Max(1, response?.ReturnItemCount ?? 1);
                            string returnItemId = response?.ReturnItemId ?? target.ItemId;
                            if (GiveItem(returnItemId, returnCount))
                            {
                                InformationManager.DisplayMessage(
                                    new InformationMessage(
                                        "[集市] 下架成功，已返还 " + returnCount + "x " + ResolveItemName(returnItemId) + "。",
                                        Colors.Green
                                    )
                                );
                            }
                            else
                            {
                                InformationManager.DisplayMessage(
                                    new InformationMessage("[集市] 下架成功，但返还物品失败，请检查背包。", Colors.Red)
                                );
                            }

                            QueueBackgroundPull();
                        }
                    );
                }
            );

            return "下架请求已发送。";
        }

        public string BuyMarketListing(string listingId)
        {
            DrainMainThreadQueue();
            EnsureReady();

            string normalized = (listingId ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return "无效的上架记录。";
            }

            TavernMarketListing target = _marketListings.FirstOrDefault(
                x => string.Equals(x?.ListingId, normalized, StringComparison.Ordinal)
            );
            if (target == null || !target.IsOpen())
            {
                return "该物品已不可购买。";
            }

            if (string.Equals(target.SellerPlayerId, _playerId, StringComparison.Ordinal))
            {
                return "不能购买自己上架的物品。";
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (IsMarketListingExpired(target, nowMs))
            {
                return "该物品已过期。";
            }

            if (!target.IsPublic(nowMs))
            {
                return "该物品仍在公示期，暂不可购买。";
            }

            int price = Math.Max(1, target.PriceDenars);
            if (Hero.MainHero == null || Hero.MainHero.Gold < price)
            {
                return "金币不足，无法购买。";
            }

            ChangeMainHeroGold(-price);

            string serverUrl = _serverUrl;
            TavernMarketBuyRequest request = new TavernMarketBuyRequest
            {
                ListingId = normalized,
                BuyerPlayerId = _playerId,
                BuyerName = _displayName,
                ChannelId = _channelId,
                ClientNonce = Guid.NewGuid().ToString("N"),
            };

            Task.Run(
                () =>
                {
                    bool ok = false;
                    TavernMarketBuyResponse response = null;
                    string error = string.Empty;
                    try
                    {
                        lock (_netLock)
                        {
                            EnsureApi();
                            _api.BaseUrl = serverUrl;
                            ok = _api.BuyMarketListing(request, out response, out error);
                        }
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = "Unexpected buy exception: " + ex.Message;
                    }

                    EnqueueMainThreadAction(
                        () =>
                        {
                            if (!ok)
                            {
                                ChangeMainHeroGold(price);
                                InformationManager.DisplayMessage(
                                    new InformationMessage("[集市] 购买失败，金币已返还: " + error, Colors.Red)
                                );
                                return;
                            }

                            string itemId2 = response?.ItemId ?? target.ItemId;
                            int itemCount2 = Math.Max(1, response?.ItemCount ?? target.ItemCount);
                            if (!GiveItem(itemId2, itemCount2))
                            {
                                ChangeMainHeroGold(price);
                                InformationManager.DisplayMessage(
                                    new InformationMessage("[集市] 购买成功但发放物品失败，金币已返还。", Colors.Red)
                                );
                                return;
                            }

                            string sellerName = response?.SellerName ?? target.SellerName ?? "Unknown";
                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    "[集市] 购买成功: "
                                        + itemCount2
                                        + "x "
                                        + ResolveItemName(itemId2)
                                        + "（卖家 "
                                        + sellerName
                                        + "，价格 "
                                        + price
                                        + "）",
                                    Colors.Green
                                )
                            );
                            QueueBackgroundPull();
                        }
                    );
                }
            );

            return "购买请求已发送。";
        }

        public string SendItemToPlayer(string targetPlayerName, string itemId, int count)
        {
            DrainMainThreadQueue();
            EnsureReady();

            string target = (targetPlayerName ?? string.Empty).Trim();
            CalradiaTavernDebug.Trace(
                "Behavior",
                "SendItemToPlayer begin target=" + target + " itemId=" + (itemId ?? string.Empty) + " count=" + count
            );
            if (target.Length < 2)
            {
                CalradiaTavernDebug.Trace("Behavior", "SendItemToPlayer reject: target too short");
                return "Target player name must be at least 2 characters.";
            }
            if (IsReservedPseudoPlayerName(target))
            {
                CalradiaTavernDebug.Trace("Behavior", "SendItemToPlayer reject: pseudo target=" + target);
                return "Send failed: invalid target player.";
            }

            if (string.Equals(target, _displayName, StringComparison.OrdinalIgnoreCase))
            {
                CalradiaTavernDebug.Trace("Behavior", "SendItemToPlayer reject: self target");
                return "Cannot send to yourself.";
            }

            if (count <= 0)
            {
                CalradiaTavernDebug.Trace("Behavior", "SendItemToPlayer reject: invalid count");
                return "Count must be greater than 0.";
            }

            ItemObject previewItem = FindItem(itemId);
            if (!TryValidateDirectTradeItem(previewItem, out string validateError))
            {
                CalradiaTavernDebug.Trace("Behavior", "SendItemToPlayer reject: validateError=" + validateError);
                return "Send failed: " + validateError;
            }

            if (!TryTakeItem(itemId, count, out ItemObject item, out string takeError))
            {
                CalradiaTavernDebug.Trace("Behavior", "SendItemToPlayer reject: takeError=" + takeError);
                return "Send failed: " + takeError;
            }

            if (IsLocalTradeBotName(target))
            {
                GiveItem(item.StringId, count);
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string itemName = item.Name?.ToString() ?? item.StringId;
                AddChatLine(
                    "sys_bot_" + Guid.NewGuid().ToString("N"),
                    "系统",
                    "交易机器人测试: 已接收并退回 " + count + "x " + itemName,
                    nowMs,
                    false
                );
                InformationManager.DisplayMessage(
                    new InformationMessage(
                        "[Calradia Tavern] 与交易机器人测试成功（已退回物品）。",
                        Colors.Green
                    )
                );
                NotifyStateChanged();
                CalradiaTavernDebug.Trace("Behavior", "SendItemToPlayer bot success");
                return "机器人测试交易成功（物品已退回）。";
            }

            TavernGiftRequestCreateRequest request = new TavernGiftRequestCreateRequest
            {
                ChannelId = _channelId,
                FromPlayerId = _playerId,
                FromPlayerName = _displayName,
                TargetPlayerName = target,
                ItemId = item.StringId,
                Count = count,
                ClientNonce = Guid.NewGuid().ToString("N"),
            };
            string sentItemId = item.StringId;
            string sentItemName = item.Name?.ToString() ?? item.StringId;
            string serverUrl = _serverUrl;

            Task.Run(
                () =>
                {
                    bool ok = false;
                    TavernGiftRequestCreateResponse response = null;
                    string error = string.Empty;

                    try
                    {
                        lock (_netLock)
                        {
                            EnsureApi();
                            _api.BaseUrl = serverUrl;
                            ok = _api.CreateGiftRequest(request, out response, out error);
                        }
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = "Unexpected trade exception: " + ex.Message;
                    }

                    EnqueueMainThreadAction(
                        () =>
                        {
                            if (!ok)
                            {
                                GiveItem(sentItemId, count);
                                string reason = NormalizeGiftError(error);
                                InformationManager.DisplayMessage(
                                    new InformationMessage(
                                        "[Calradia Tavern] 赠送失败，物品已返还: " + reason,
                                        Colors.Red
                                    )
                                );
                                return;
                            }

                            InformationManager.DisplayMessage(
                                new InformationMessage(
                                    "[Calradia Tavern] 已发起赠送请求: "
                                        + count
                                        + "x "
                                        + sentItemName
                                        + " -> "
                                        + (response?.TargetPlayerName ?? target)
                                        + "（等待对方确认）",
                                    Colors.Green
                                )
                            );
                        }
                    );
                }
            );

            return "Sending in background...";
        }

        private static bool IsLocalTradeBotName(string playerName)
        {
            string normalized = (playerName ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return false;
            }

            return LocalTradeBotAliases.Any(alias =>
                string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase)
            );
        }

        private static bool IsReservedPseudoPlayerName(string playerName)
        {
            string normalized = (playerName ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return true;
            }

            if (string.Equals(normalized, SystemPseudoPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(normalized, "system", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(normalized, "server", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private string GetCurrentClientStateCode()
        {
            if (_presenceActive)
            {
                return "tavern";
            }

            object state = Game.Current?.GameStateManager?.ActiveState;
            if (state is MapState)
            {
                return "map";
            }
            if (state is MissionState)
            {
                return "mission";
            }

            return "other";
        }

        private static string NormalizeGiftError(string error)
        {
            string raw = (error ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return "未知错误";
            }

            if (raw.IndexOf(TargetBlockedYouMessage, StringComparison.Ordinal) >= 0)
            {
                return TargetBlockedYouMessage;
            }

            if (raw.IndexOf(TargetNotOnMapMessage, StringComparison.Ordinal) >= 0)
            {
                return TargetNotOnMapMessage;
            }

            return raw;
        }

        public string SetDisplayName(string value)
        {
            EnsureReady();

            string next = (value ?? string.Empty).Trim();
            if (next.Length < 1)
            {
                return "Display name cannot be empty.";
            }

            if (next.Length > 20)
            {
                next = next.Substring(0, 20);
            }

            _displayName = next;
            if (CalradiaTavernSettings.Instance != null)
            {
                CalradiaTavernSettings.Instance.UserName = _displayName;
            }
            TryUpsertPlayer(false);
            NotifyStateChanged();
            return "Display name set to: " + _displayName;
        }

        public string SetServerUrl(string value)
        {
            string next = NormalizeServerUrl(value);
            if (string.IsNullOrWhiteSpace(next))
            {
                return "Server URL must start with http:// or https://";
            }

            _serverUrl = next;
            _giftRequestsEndpointUnavailable = false;
            _blockedPlayersEndpointUnavailable = false;
            if (CalradiaTavernSettings.Instance != null)
            {
                CalradiaTavernSettings.Instance.ServerUrl = next;
            }

            EnsureApi();
            NotifyStateChanged();
            return "Server switched to: " + _serverUrl;
        }

        private void OnTick(float dt)
        {
            DrainMainThreadQueue();
            EnsureReady();
            ApplyConfiguredServerUrl();
            ApplyConfiguredDisplayName();

            _pollElapsed += Math.Max(0f, dt);
            _registerElapsed += Math.Max(0f, dt);

            if (_registerElapsed >= RegisterIntervalSeconds)
            {
                _registerElapsed = 0f;
                TryUpsertPlayer(false, true);
            }

            if (_pollElapsed < PollIntervalSeconds)
            {
                return;
            }

            _pollElapsed = 0f;
            QueueBackgroundPull();
        }

        private int PullChat()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastChatUnixMs > nowMs + 60_000L)
            {
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "PullChat cursor reset (future timestamp). last="
                        + _lastChatUnixMs
                        + " now="
                        + nowMs
                );
                _lastChatUnixMs = 0;
            }

            long afterMs = Math.Max(0, _lastChatUnixMs - 1);
            if (
                !_api.PullChat(
                    _channelId,
                    afterMs,
                    out List<TavernChatMessageDto> messages,
                    out string error
                )
            )
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    CalradiaTavernDebug.Trace(
                        "Behavior",
                        "PullChat failed. server="
                            + _serverUrl
                            + " channel="
                            + _channelId
                            + " afterMs="
                            + afterMs
                            + " error="
                            + error
                    );
                }
                return 0;
            }

            if (messages == null || messages.Count == 0)
            {
                return 0;
            }

            List<TavernChatMessageDto> orderedMessages = messages
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.MessageId))
                .OrderBy(x => NormalizeUnixTimeMs(x.UnixTimeMs))
                .ThenBy(x => x.MessageId ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            int count = 0;
            foreach (TavernChatMessageDto msg in orderedMessages)
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.MessageId))
                {
                    continue;
                }

                long msgUnixMs = NormalizeUnixTimeMs(msg.UnixTimeMs);
                _lastChatUnixMs = Math.Max(_lastChatUnixMs, msgUnixMs);
                if (_seenChatIds.Contains(msg.MessageId))
                {
                    continue;
                }

                RememberId(_seenChatIds, msg.MessageId);
                bool samePlayerId = string.Equals(msg.PlayerId, _playerId, StringComparison.Ordinal);
                string msgNameNorm = NormalizeDisplayName(msg.PlayerName) ?? string.Empty;
                string selfNameNorm = NormalizeDisplayName(_displayName) ?? string.Empty;
                bool sameDisplayName = string.Equals(
                    msgNameNorm,
                    selfNameNorm,
                    StringComparison.OrdinalIgnoreCase
                );
                bool isSelf = samePlayerId && (string.IsNullOrWhiteSpace(msg.PlayerName) || sameDisplayName);
                string senderName = string.IsNullOrWhiteSpace(msg.PlayerName)
                    ? (isSelf ? _displayName : "Anonymous")
                    : msg.PlayerName;
                if (string.IsNullOrWhiteSpace(senderName))
                {
                    senderName = isSelf ? "Me" : "Anonymous";
                }

                if (isSelf)
                {
                    TryResolvePendingLocalChatByEcho(msg.Text ?? string.Empty, msgUnixMs);
                }
                AddChatLine(
                    msg.MessageId,
                    senderName,
                    msg.Text ?? string.Empty,
                    msgUnixMs,
                    isSelf
                );

                if (!isSelf)
                {
                    _unreadChatCount++;
                    if (ShowChatToastInNativeFeed)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(
                                FormatChatToast(senderName, msg.Text ?? string.Empty, msgUnixMs),
                                Colors.Cyan
                            )
                        );
                    }
                    count++;
                }
            }

            if (count > 0)
            {
                NotifyStateChanged();
            }
            return count;
        }

        private int PullDeliveries()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastDeliveryUnixMs > nowMs + 60_000L)
            {
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "PullDeliveries cursor reset (future timestamp). last="
                        + _lastDeliveryUnixMs
                        + " now="
                        + nowMs
                );
                _lastDeliveryUnixMs = 0;
            }

            long afterMs = Math.Max(0, _lastDeliveryUnixMs - 1);
            if (
                !_api.PullDeliveries(
                    _playerId,
                    afterMs,
                    out List<TavernDeliveryDto> deliveries,
                    out string error
                )
            )
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    CalradiaTavernDebug.Trace(
                        "Behavior",
                        "PullDeliveries failed. server="
                            + _serverUrl
                            + " playerId="
                            + _playerId
                            + " afterMs="
                            + afterMs
                            + " error="
                            + error
                    );
                }
                return 0;
            }

            if (deliveries == null || deliveries.Count == 0)
            {
                return 0;
            }

            int count = 0;
            foreach (TavernDeliveryDto delivery in deliveries.OrderBy(x => NormalizeUnixTimeMs(x?.UnixTimeMs ?? 0L)))
            {
                if (delivery == null || string.IsNullOrWhiteSpace(delivery.DeliveryId))
                {
                    continue;
                }

                long deliveryUnixMs = NormalizeUnixTimeMs(delivery.UnixTimeMs);
                _lastDeliveryUnixMs = Math.Max(_lastDeliveryUnixMs, deliveryUnixMs);
                if (_seenDeliveryIds.Contains(delivery.DeliveryId))
                {
                    continue;
                }

                RememberId(_seenDeliveryIds, delivery.DeliveryId);
                if (!TryApplyDeliveryPayload(delivery, out string itemName, out int displayCount))
                {
                    continue;
                }

                string fromName = string.IsNullOrWhiteSpace(delivery.FromPlayerName)
                    ? "Anonymous"
                    : delivery.FromPlayerName;
                bool compensated = string.Equals(
                    itemName,
                    "未知物品（已折算）",
                    StringComparison.Ordinal
                );
                string note = (delivery.Note ?? string.Empty).Trim();
                bool giftRejected = note.StartsWith("gift_rejected:", StringComparison.OrdinalIgnoreCase);
                string rejectReason = giftRejected
                    ? note.Substring("gift_rejected:".Length).Trim()
                    : string.Empty;
                if (giftRejected && rejectReason.Length == 0)
                {
                    rejectReason = "对方拒绝接收";
                }

                string toast;
                string chat;
                if (giftRejected)
                {
                    toast = "[卡拉迪亚酒馆] 赠送未完成，物品已返还："
                        + fromName
                        + "（原因："
                        + rejectReason
                        + "）。";
                    chat = "赠送未完成，"
                        + fromName
                        + " 未接收你的物品，已返还。原因："
                        + rejectReason;
                }
                else if (compensated)
                {
                    toast = "[卡拉迪亚酒馆] " + fromName + " 赠送了你无法识别的物品，已折算 " + displayCount + " 第纳尔。";
                    chat = fromName + " 赠送了你无法识别的物品，已折算 " + displayCount + " 第纳尔";
                }
                else
                {
                    toast = "[卡拉迪亚酒馆] " + fromName + " 赠送了你 " + displayCount + "x " + itemName + "。";
                    chat = fromName + " 赠送了你 " + displayCount + "x " + itemName;
                }

                InformationManager.DisplayMessage(
                    new InformationMessage(toast, Colors.Green)
                );
                AddChatLine(
                    "sys_" + delivery.DeliveryId,
                    "系统",
                    chat,
                    deliveryUnixMs,
                    false
                );
                count++;
            }

            if (count > 0)
            {
                NotifyStateChanged();
            }
            return count;
        }

        private void QueueBackgroundSendChat(
            TavernSendChatRequest request,
            string selfName,
            string text
        )
        {
            string serverUrl = _serverUrl;
            long startedMs = CalradiaTavernDebug.NowMs;
            Task.Run(
                () =>
                {
                    bool ok = false;
                    TavernSendChatResponse response = null;
                    string error = string.Empty;
                    try
                    {
                        lock (_netLock)
                        {
                            EnsureApi();
                            _api.BaseUrl = serverUrl;
                            ok = _api.SendChat(request, out response, out error);
                        }
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        error = "Unexpected send exception: " + ex.Message;
                    }

                    EnqueueMainThreadAction(
                        () =>
                        {
                            long totalMs = CalradiaTavernDebug.NowMs - startedMs;
                            if (!ok)
                            {
                                TryRemovePendingLocalChatByNonce(request.ClientNonce);
                                InformationManager.DisplayMessage(
                                    new InformationMessage(
                                        "[Calradia Tavern] Send failed: " + error,
                                        Colors.Red
                                    )
                                );
                                CalradiaTavernDebug.Trace("Behavior", "SendChat failed totalMs=" + totalMs + " error=" + error);
                                return;
                            }

                            if (response != null)
                            {
                                TryRemovePendingLocalChatByNonce(request.ClientNonce);
                                long sentUnixMs = response.UnixTimeMs <= 0
                                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    : response.UnixTimeMs;
                                _lastChatUnixMs = Math.Max(_lastChatUnixMs, sentUnixMs);
                                RememberId(_seenChatIds, response.MessageId);
                                AddChatLine(
                                    response.MessageId,
                                    selfName,
                                    text,
                                    sentUnixMs,
                                    true
                                );
                                if (totalMs >= 200)
                                {
                                    CalradiaTavernDebug.Trace("Behavior", "SendChat ok totalMs=" + totalMs + " hasResponse=true");
                                }
                                return;
                            }

                            // Keep local optimistic line; server may still surface it via PullChat.
                            ClearPendingNonceOnly(request.ClientNonce);
                            if (totalMs >= 200)
                            {
                                CalradiaTavernDebug.Trace("Behavior", "SendChat ok totalMs=" + totalMs + " hasResponse=false");
                            }
                        }
                    );
                }
            );
        }

        private void QueueBackgroundPull()
        {
            if (Interlocked.CompareExchange(ref _pullInFlight, 1, 0) != 0)
            {
                return;
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastChatUnixMs > nowMs + 60_000L)
            {
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "PullChat cursor reset (future timestamp). last="
                        + _lastChatUnixMs
                        + " now="
                        + nowMs
                );
                _lastChatUnixMs = 0;
            }

            if (_lastDeliveryUnixMs > nowMs + 60_000L)
            {
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "PullDeliveries cursor reset (future timestamp). last="
                        + _lastDeliveryUnixMs
                        + " now="
                        + nowMs
                );
                _lastDeliveryUnixMs = 0;
            }

            string channelId = _channelId;
            string playerId = _playerId;
            string serverUrl = _serverUrl;
            long chatAfterMs = Math.Max(0, _lastChatUnixMs - 1);
            long deliveryAfterMs = Math.Max(0, _lastDeliveryUnixMs - 1);
            bool shouldPullPlayers = _presenceActive;
            long startedMs = CalradiaTavernDebug.NowMs;

            Task.Run(
                () =>
                {
                    try
                    {
                        bool chatOk;
                        bool deliveryOk;
                        bool giftOk;
                        bool playersOk;
                        bool blockedOk;
                        bool marketOk;
                        List<TavernChatMessageDto> messages;
                        List<TavernDeliveryDto> deliveries;
                        List<TavernGiftRequestDto> giftRequests;
                        List<TavernPlayerPresenceDto> players;
                        List<TavernBlockedPlayerDto> blockedPlayers;
                        List<TavernMarketListingDto> marketListings;
                        string chatError;
                        string deliveryError;
                        string giftError;
                        string playersError;
                        string blockedError;
                        string marketError;

                        lock (_netLock)
                        {
                            EnsureApi();
                            _api.BaseUrl = serverUrl;
                            chatOk = _api.PullChat(channelId, chatAfterMs, out messages, out chatError);
                            deliveryOk = _api.PullDeliveries(
                                playerId,
                                deliveryAfterMs,
                                out deliveries,
                                out deliveryError
                            );
                            if (_giftRequestsEndpointUnavailable)
                            {
                                giftOk = true;
                                giftRequests = new List<TavernGiftRequestDto>();
                                giftError = string.Empty;
                            }
                            else
                            {
                                giftOk = _api.PullGiftRequests(playerId, out giftRequests, out giftError);
                                if (!giftOk && IsEndpointNotFoundError(giftError))
                                {
                                    _giftRequestsEndpointUnavailable = true;
                                    giftOk = true;
                                    giftRequests = new List<TavernGiftRequestDto>();
                                    CalradiaTavernDebug.Trace(
                                        "Behavior",
                                        "PullGiftRequests endpoint not found on server; disable gift-request polling until reload. error="
                                            + giftError
                                    );
                                }
                            }
                            if (shouldPullPlayers)
                            {
                                playersOk = _api.ListPlayers(
                                    channelId,
                                    OnlinePlayerActiveWindowSec,
                                    MaxOnlinePlayerCache,
                                    out players,
                                    out playersError
                                );
                            }
                            else
                            {
                                playersOk = true;
                                    players = new List<TavernPlayerPresenceDto>();
                                    playersError = string.Empty;
                                }
                            if (_blockedPlayersEndpointUnavailable)
                            {
                                blockedOk = true;
                                blockedPlayers = new List<TavernBlockedPlayerDto>();
                                blockedError = string.Empty;
                            }
                            else
                            {
                                blockedOk = _api.ListBlockedPlayers(
                                    channelId,
                                    playerId,
                                    out blockedPlayers,
                                    out blockedError
                                );
                                if (!blockedOk && IsEndpointNotFoundError(blockedError))
                                {
                                    _blockedPlayersEndpointUnavailable = true;
                                    blockedOk = true;
                                    blockedPlayers = new List<TavernBlockedPlayerDto>();
                                    CalradiaTavernDebug.Trace(
                                        "Behavior",
                                        "PullBlocked endpoint not found on server; disable blocked-player polling until reload. error="
                                            + blockedError
                                    );
                                }
                            }
                            marketOk = _api.ListMarketListings(channelId, out marketListings, out marketError);
                        }

                        EnqueueMainThreadAction(
                            () =>
                            {
                                try
                                {
                                    if (!chatOk)
                                    {
                                        if (!string.IsNullOrWhiteSpace(chatError))
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullChat failed. server="
                                                    + serverUrl
                                                    + " channel="
                                                    + channelId
                                                    + " afterMs="
                                                    + chatAfterMs
                                                    + " error="
                                                    + chatError
                                            );
                                        }
                                    }
                                    else
                                    {
                                        int appliedChat = ApplyPulledChatMessages(messages);
                                        long totalMs = CalradiaTavernDebug.NowMs - startedMs;
                                        if (appliedChat > 0 || totalMs >= 400)
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullChat applied=" + appliedChat + " totalMs=" + totalMs
                                            );
                                        }
                                    }

                                    if (!deliveryOk)
                                    {
                                        if (!string.IsNullOrWhiteSpace(deliveryError))
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullDeliveries failed. server="
                                                    + serverUrl
                                                    + " playerId="
                                                    + playerId
                                                    + " afterMs="
                                                    + deliveryAfterMs
                                                    + " error="
                                                    + deliveryError
                                            );
                                        }
                                    }
                                    else
                                    {
                                        int appliedDelivery = ApplyPulledDeliveries(deliveries);
                                        long totalMs = CalradiaTavernDebug.NowMs - startedMs;
                                        if (appliedDelivery > 0 || totalMs >= 400)
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullDeliveries applied=" + appliedDelivery + " totalMs=" + totalMs
                                            );
                                        }
                                    }

                                    if (!giftOk)
                                    {
                                        if (!string.IsNullOrWhiteSpace(giftError))
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullGiftRequests failed. playerId="
                                                    + playerId
                                                    + " error="
                                                    + giftError
                                            );
                                        }
                                    }
                                    else
                                    {
                                        int appliedGiftRequests = ApplyPulledGiftRequests(giftRequests);
                                        if (appliedGiftRequests > 0)
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullGiftRequests applied=" + appliedGiftRequests
                                            );
                                        }
                                    }

                                    if (!shouldPullPlayers)
                                    {
                                        // Presence disabled: keep online list empty/offline.
                                    }
                                    else if (!playersOk)
                                    {
                                        if (!string.IsNullOrWhiteSpace(playersError))
                                        {
                                            long nowMs2 = CalradiaTavernDebug.NowMs;
                                            if (nowMs2 - _lastPullPlayersErrorLogMs >= 10_000L)
                                            {
                                                _lastPullPlayersErrorLogMs = nowMs2;
                                                CalradiaTavernDebug.Trace(
                                                    "Behavior",
                                                    "PullPlayers failed. server="
                                                        + serverUrl
                                                        + " channel="
                                                        + channelId
                                                        + " error="
                                                        + playersError
                                                );
                                            }
                                        }
                                    }
                                    else
                                    {
                                        int appliedPlayers = ApplyPulledPlayers(players);
                                        if (appliedPlayers > 0)
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullPlayers applied=" + appliedPlayers
                                            );
                                        }
                                    }

                                    if (!blockedOk)
                                    {
                                        if (!string.IsNullOrWhiteSpace(blockedError))
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullBlocked failed. playerId="
                                                    + playerId
                                                    + " error="
                                                    + blockedError
                                            );
                                        }
                                    }
                                    else
                                    {
                                        ApplyPulledBlockedPlayers(blockedPlayers);
                                    }

                                    if (!marketOk)
                                    {
                                        if (!string.IsNullOrWhiteSpace(marketError))
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullMarket failed. server="
                                                    + serverUrl
                                                    + " channel="
                                                    + channelId
                                                    + " error="
                                                    + marketError
                                            );
                                        }
                                    }
                                    else
                                    {
                                        int appliedMarket = ApplyPulledMarketListings(marketListings);
                                        if (appliedMarket > 0)
                                        {
                                            CalradiaTavernDebug.Trace(
                                                "Behavior",
                                                "PullMarket applied=" + appliedMarket
                                            );
                                        }
                                    }
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _pullInFlight, 0);
                                }
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        EnqueueMainThreadAction(
                            () =>
                            {
                                CalradiaTavernDebug.ReportException("Behavior.QueueBackgroundPull", ex);
                                Interlocked.Exchange(ref _pullInFlight, 0);
                            }
                        );
                    }
                }
            );
        }

        private int ApplyPulledChatMessages(List<TavernChatMessageDto> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return 0;
            }

            List<TavernChatMessageDto> orderedMessages = messages
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.MessageId))
                .OrderBy(x => NormalizeUnixTimeMs(x.UnixTimeMs))
                .ThenBy(x => x.MessageId ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            if (orderedMessages.Count <= 0)
            {
                return 0;
            }

            bool payloadNonMonotonic = false;
            long payloadFirstMs = 0;
            long payloadLastMs = 0;
            long payloadPrevMs = 0;
            string payloadFirstId = string.Empty;
            string payloadLastId = string.Empty;
            for (int i = 0; i < orderedMessages.Count; i++)
            {
                TavernChatMessageDto payload = orderedMessages[i];
                if (payload == null || string.IsNullOrWhiteSpace(payload.MessageId))
                {
                    continue;
                }

                long payloadMs = NormalizeUnixTimeMs(payload.UnixTimeMs);
                if (payloadMs <= 0)
                {
                    payloadMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }

                if (payloadFirstMs == 0)
                {
                    payloadFirstMs = payloadMs;
                    payloadFirstId = payload.MessageId ?? string.Empty;
                }
                else if (payloadMs < payloadPrevMs)
                {
                    payloadNonMonotonic = true;
                }

                payloadPrevMs = payloadMs;
                payloadLastMs = payloadMs;
                payloadLastId = payload.MessageId ?? string.Empty;
            }

            long diagNowMs = CalradiaTavernDebug.NowMs;
            if (payloadNonMonotonic || diagNowMs >= _nextChatOrderDiagLogMs)
            {
                _nextChatOrderDiagLogMs = diagNowMs + 5000L;
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "ApplyPulledChatMessages payloadCount="
                        + orderedMessages.Count
                        + " firstId="
                        + payloadFirstId
                        + " firstMs="
                        + payloadFirstMs
                        + " lastId="
                        + payloadLastId
                        + " lastMs="
                        + payloadLastMs
                        + " nonMonotonic="
                        + payloadNonMonotonic
                );
            }

            int count = 0;
            foreach (TavernChatMessageDto msg in orderedMessages)
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.MessageId))
                {
                    continue;
                }

                long msgUnixMs = NormalizeUnixTimeMs(msg.UnixTimeMs);
                _lastChatUnixMs = Math.Max(_lastChatUnixMs, msgUnixMs);
                if (_seenChatIds.Contains(msg.MessageId))
                {
                    continue;
                }

                RememberId(_seenChatIds, msg.MessageId);
                bool samePlayerId = string.Equals(msg.PlayerId, _playerId, StringComparison.Ordinal);
                string msgNameNorm = NormalizeDisplayName(msg.PlayerName) ?? string.Empty;
                string selfNameNorm = NormalizeDisplayName(_displayName) ?? string.Empty;
                bool sameDisplayName = string.Equals(
                    msgNameNorm,
                    selfNameNorm,
                    StringComparison.OrdinalIgnoreCase
                );
                bool isSelf = samePlayerId && (string.IsNullOrWhiteSpace(msg.PlayerName) || sameDisplayName);
                string senderName = string.IsNullOrWhiteSpace(msg.PlayerName)
                    ? (isSelf ? _displayName : "Anonymous")
                    : msg.PlayerName;
                if (string.IsNullOrWhiteSpace(senderName))
                {
                    senderName = isSelf ? "Me" : "Anonymous";
                }

                if (isSelf)
                {
                    TryResolvePendingLocalChatByEcho(msg.Text ?? string.Empty, msgUnixMs);
                }
                AddChatLine(
                    msg.MessageId,
                    senderName,
                    msg.Text ?? string.Empty,
                    msgUnixMs,
                    isSelf
                );

                if (!isSelf)
                {
                    _unreadChatCount++;
                    if (ShowChatToastInNativeFeed)
                    {
                        InformationManager.DisplayMessage(
                            new InformationMessage(
                                FormatChatToast(senderName, msg.Text ?? string.Empty, msgUnixMs),
                                Colors.Cyan
                            )
                        );
                    }
                    count++;
                }
            }

            if (count > 0)
            {
                NotifyStateChanged();
            }
            return count;
        }

        private int ApplyPulledPlayers(List<TavernPlayerPresenceDto> players)
        {
            if (players == null)
            {
                return 0;
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Dictionary<string, long> nextPresence = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase
            );

            foreach (TavernPlayerPresenceDto player in players)
            {
                if (player == null)
                {
                    continue;
                }

                if (!string.Equals(player.ChannelId ?? FixedChannelId, _channelId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!player.IsTavernActive)
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(player.PlayerName)
                    ? "Anonymous"
                    : player.PlayerName.Trim();
                if (name.Length == 0)
                {
                    continue;
                }
                if (IsReservedPseudoPlayerName(name))
                {
                    continue;
                }

                long lastSeenMs = NormalizeUnixTimeMs(player.LastSeenUnixTimeMs);
                if (lastSeenMs <= 0)
                {
                    continue;
                }

                if (!nextPresence.TryGetValue(name, out long existingLastSeen) || lastSeenMs > existingLastSeen)
                {
                    nextPresence[name] = lastSeenMs;
                }
            }

            List<string> next = nextPresence
                .OrderByDescending(x => x.Value)
                .Select(x => x.Key)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(MaxOnlinePlayerCache)
                .ToList();

            if (
                _onlinePlayerNames.SequenceEqual(next, StringComparer.Ordinal)
                && AreOnlinePresenceEqual(_onlinePlayerLastSeenUnixMs, nextPresence)
            )
            {
                return 0;
            }

            _onlinePlayerNames = next;
            _onlinePlayerLastSeenUnixMs = nextPresence;
            NotifyStateChanged();
            return 1;
        }

        private void ApplyPulledBlockedPlayers(List<TavernBlockedPlayerDto> blockedPlayers)
        {
            if (blockedPlayers == null)
            {
                return;
            }

            HashSet<string> next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < blockedPlayers.Count; i++)
            {
                TavernBlockedPlayerDto current = blockedPlayers[i];
                if (current == null)
                {
                    continue;
                }

                string name = NormalizeDisplayName(current.PlayerName) ?? string.Empty;
                if (name.Length > 0)
                {
                    next.Add(name);
                }
            }

            bool changed = !_blockedPlayerNames.SetEquals(next);
            if (!changed)
            {
                return;
            }

            _blockedPlayerNames.Clear();
            foreach (string item in next)
            {
                _blockedPlayerNames.Add(item);
            }
            _blockedPlayerNamesSync = _blockedPlayerNames
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private int ApplyPulledGiftRequests(List<TavernGiftRequestDto> giftRequests)
        {
            if (giftRequests == null || giftRequests.Count == 0)
            {
                return 0;
            }

            int count = 0;
            foreach (TavernGiftRequestDto request in giftRequests)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.RequestId))
                {
                    continue;
                }
                if (!string.Equals(request.ToPlayerId ?? string.Empty, _playerId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!string.Equals(request.Status ?? "pending", "pending", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (_pendingGiftRequestIds.Contains(request.RequestId))
                {
                    continue;
                }

                _pendingGiftRequestIds.Add(request.RequestId);
                HandleIncomingGiftRequest(request);
                count++;
            }

            return count;
        }

        private void HandleIncomingGiftRequest(TavernGiftRequestDto request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RequestId))
            {
                return;
            }

            string itemId = (request.ItemId ?? string.Empty).Trim();
            int count = Math.Max(1, request.Count);
            string fromName = string.IsNullOrWhiteSpace(request.FromPlayerName)
                ? "Anonymous"
                : request.FromPlayerName.Trim();
            string itemName = ResolveItemName(itemId);

            if (!CanReceiveGiftPopupInCurrentState())
            {
                QueueBackgroundRespondGiftRequest(request.RequestId, false, TargetNotOnMapMessage);
                return;
            }

            string body = fromName
                + " 想赠送你 "
                + count.ToString(CultureInfo.InvariantCulture)
                + "x "
                + itemName
                + "，是否接收？";

            InformationManager.ShowInquiry(
                new InquiryData(
                    "收到赠送请求",
                    body,
                    true,
                    true,
                    "同意",
                    "拒绝",
                    () => QueueBackgroundRespondGiftRequest(request.RequestId, true, string.Empty),
                    () => QueueBackgroundRespondGiftRequest(request.RequestId, false, "对方拒绝接收")
                ),
                true,
                true
            );
        }

        private void QueueBackgroundRespondGiftRequest(string requestId, bool accepted, string reason)
        {
            string safeRequestId = (requestId ?? string.Empty).Trim();
            if (safeRequestId.Length == 0)
            {
                return;
            }

            string serverUrl = _serverUrl;
            string safeReason = (reason ?? string.Empty).Trim();
            string playerId = _playerId;
            Task.Run(
                () =>
                {
                    bool ok;
                    string error;
                    lock (_netLock)
                    {
                        EnsureApi();
                        _api.BaseUrl = serverUrl;
                        ok = _api.RespondGiftRequest(
                            new TavernGiftRequestRespondRequest
                            {
                                RequestId = safeRequestId,
                                PlayerId = playerId,
                                Accepted = accepted,
                                Reason = safeReason,
                                ClientNonce = Guid.NewGuid().ToString("N"),
                            },
                            out TavernGiftRequestRespondResponse _,
                            out error
                        );
                    }

                    EnqueueMainThreadAction(
                        () =>
                        {
                            if (!ok)
                            {
                                CalradiaTavernDebug.Trace(
                                    "Behavior",
                                    "RespondGiftRequest failed id=" + safeRequestId + " error=" + error
                                );
                            }

                            _pendingGiftRequestIds.Remove(safeRequestId);
                            QueueBackgroundPull();
                        }
                    );
                }
            );
        }

        private bool CanReceiveGiftPopupInCurrentState()
        {
            if (_presenceActive)
            {
                return true;
            }

            return Game.Current?.GameStateManager?.ActiveState is MapState;
        }

        private int ApplyPulledDeliveries(List<TavernDeliveryDto> deliveries)
        {
            if (deliveries == null || deliveries.Count == 0)
            {
                return 0;
            }

            int count = 0;
            foreach (TavernDeliveryDto delivery in deliveries.OrderBy(x => NormalizeUnixTimeMs(x?.UnixTimeMs ?? 0L)))
            {
                if (delivery == null || string.IsNullOrWhiteSpace(delivery.DeliveryId))
                {
                    continue;
                }

                long deliveryUnixMs = NormalizeUnixTimeMs(delivery.UnixTimeMs);
                _lastDeliveryUnixMs = Math.Max(_lastDeliveryUnixMs, deliveryUnixMs);
                if (_seenDeliveryIds.Contains(delivery.DeliveryId))
                {
                    continue;
                }

                RememberId(_seenDeliveryIds, delivery.DeliveryId);
                if (!TryApplyDeliveryPayload(delivery, out string itemName, out int displayCount))
                {
                    continue;
                }

                string fromName = string.IsNullOrWhiteSpace(delivery.FromPlayerName)
                    ? "Anonymous"
                    : delivery.FromPlayerName;
                bool compensated = string.Equals(
                    itemName,
                    "未知物品（已折算）",
                    StringComparison.Ordinal
                );
                string toast = compensated
                    ? "[卡拉迪亚酒馆] " + fromName + " 赠送了你无法识别的物品，已折算 " + displayCount + " 第纳尔。"
                    : "[卡拉迪亚酒馆] " + fromName + " 赠送了你 " + displayCount + "x " + itemName + "。";
                string chat = compensated
                    ? fromName + " 赠送了你无法识别的物品，已折算 " + displayCount + " 第纳尔"
                    : fromName + " 赠送了你 " + displayCount + "x " + itemName;

                InformationManager.DisplayMessage(
                    new InformationMessage(toast, Colors.Green)
                );
                AddChatLine(
                    "sys_" + delivery.DeliveryId,
                    "系统",
                    chat,
                    deliveryUnixMs,
                    false
                );
                count++;
            }

            if (count > 0)
            {
                NotifyStateChanged();
            }
            return count;
        }

        private int ApplyPulledMarketListings(List<TavernMarketListingDto> listings)
        {
            if (listings == null)
            {
                return 0;
            }

            List<TavernMarketListing> next = listings
                .Where(x => x != null)
                .Select(
                    x =>
                        new TavernMarketListing
                        {
                            ListingId = x.ListingId ?? string.Empty,
                            SellerPlayerId = x.SellerPlayerId ?? string.Empty,
                            SellerName = string.IsNullOrWhiteSpace(x.SellerName)
                                ? "Anonymous"
                                : x.SellerName.Trim(),
                            ItemId = x.ItemId ?? string.Empty,
                            ItemName = string.IsNullOrWhiteSpace(x.ItemName)
                                ? (x.ItemId ?? string.Empty)
                                : x.ItemName.Trim(),
                            ItemCount = Math.Max(1, x.ItemCount),
                            PriceDenars = Math.Max(1, x.PriceDenars),
                            Category = ParseMarketCategoryFromServerCode(x.Category),
                            Status = x.Status ?? "open",
                            CreatedUnixTimeMs = NormalizeUnixTimeMs(x.CreatedUnixTimeMs),
                            PublicUnixTimeMs = NormalizeUnixTimeMs(x.PublicUnixTimeMs),
                            UpdatedUnixTimeMs = NormalizeUnixTimeMs(x.UpdatedUnixTimeMs),
                            BuyerPlayerId = x.BuyerPlayerId ?? string.Empty,
                            BuyerName = x.BuyerName ?? string.Empty,
                        }
                )
                .Where(x => !string.IsNullOrWhiteSpace(x.ListingId) && x.IsOpen())
                .OrderByDescending(x => x.CreatedUnixTimeMs)
                .Take(MaxMarketListingsCache)
                .ToList();

            if (AreMarketListingsEqual(_marketListings, next))
            {
                return 0;
            }

            _marketListings = next;
            NotifyStateChanged();
            return 1;
        }

        private void EnqueueMainThreadAction(Action action)
        {
            if (action == null)
            {
                return;
            }

            int queueCount;
            lock (_mainThreadQueueLock)
            {
                _mainThreadQueue.Enqueue(action);
                queueCount = _mainThreadQueue.Count;
            }

            if (queueCount >= 25)
            {
                CalradiaTavernDebug.Trace("Behavior", "MainThreadQueue backlog=" + queueCount);
            }
        }

        private void DrainMainThreadQueue()
        {
            long startedMs = CalradiaTavernDebug.NowMs;
            int handled = 0;
            while (true)
            {
                Action action = null;
                lock (_mainThreadQueueLock)
                {
                    if (_mainThreadQueue.Count > 0)
                    {
                        action = _mainThreadQueue.Dequeue();
                    }
                }

                if (action == null)
                {
                    long elapsed = CalradiaTavernDebug.NowMs - startedMs;
                    if (handled > 0 || elapsed >= 20)
                    {
                        CalradiaTavernDebug.Trace(
                            "Behavior",
                            "DrainMainThreadQueue handled=" + handled + " elapsedMs=" + elapsed
                        );
                    }
                    return;
                }

                try
                {
                    action();
                    handled++;
                }
                catch (Exception ex)
                {
                    CalradiaTavernDebug.ReportException("Behavior.DrainMainThreadQueue", ex);
                }
            }

        }

        private long RegisterPendingLocalChat(string nonce, string playerName, string text)
        {
            string safeNonce = string.IsNullOrWhiteSpace(nonce) ? Guid.NewGuid().ToString("N") : nonce;
            string localMessageId = "local_" + safeNonce;
            long createdMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _pendingLocalChatsByNonce[safeNonce] = new PendingLocalChatEntry
            {
                LocalMessageId = localMessageId,
                Text = (text ?? string.Empty).Trim(),
                CreatedUnixTimeMs = createdMs,
            };

            AddChatLine(localMessageId, playerName, text, createdMs, true);
            return createdMs;
        }

        private void TryRemovePendingLocalChatByNonce(string nonce)
        {
            if (string.IsNullOrWhiteSpace(nonce))
            {
                return;
            }

            if (!_pendingLocalChatsByNonce.TryGetValue(nonce, out PendingLocalChatEntry pending))
            {
                return;
            }

            _pendingLocalChatsByNonce.Remove(nonce);
            if (!string.IsNullOrWhiteSpace(pending.LocalMessageId))
            {
                RemoveChatLineById(pending.LocalMessageId);
            }
        }

        private void ClearPendingNonceOnly(string nonce)
        {
            if (string.IsNullOrWhiteSpace(nonce))
            {
                return;
            }

            _pendingLocalChatsByNonce.Remove(nonce);
        }

        private void TryResolvePendingLocalChatByEcho(string serverText, long serverUnixTimeMs)
        {
            if (_pendingLocalChatsByNonce.Count == 0)
            {
                return;
            }

            string normalizedServerText = (serverText ?? string.Empty).Trim();
            if (normalizedServerText.Length == 0)
            {
                return;
            }

            long serverMs = NormalizeUnixTimeMs(serverUnixTimeMs);
            if (serverMs <= 0)
            {
                serverMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            string matchedNonce = null;
            long bestDistance = long.MaxValue;

            foreach (KeyValuePair<string, PendingLocalChatEntry> kv in _pendingLocalChatsByNonce)
            {
                PendingLocalChatEntry pending = kv.Value;
                if (pending == null)
                {
                    continue;
                }

                if (!string.Equals(pending.Text ?? string.Empty, normalizedServerText, StringComparison.Ordinal))
                {
                    continue;
                }

                long distance = Math.Abs(serverMs - pending.CreatedUnixTimeMs);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    matchedNonce = kv.Key;
                }
            }

            if (matchedNonce == null)
            {
                return;
            }

            TryRemovePendingLocalChatByNonce(matchedNonce);
        }

        private void RemoveChatLineById(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            int idx = _chatLines.FindIndex(x => string.Equals(x?.MessageId, messageId, StringComparison.Ordinal));
            if (idx < 0)
            {
                return;
            }

            _chatLines.RemoveAt(idx);
            NotifyStateChanged();
        }

        private void AddChatLine(string messageId, string playerName, string text, long unixMs, bool isSelf)
        {
            long normalizedMs = NormalizeUnixTimeMs(unixMs);
            if (normalizedMs <= 0)
            {
                normalizedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            long tailBeforeMs = 0;
            string tailBeforeId = string.Empty;
            if (_chatLines.Count > 0)
            {
                TavernChatLine tailBefore = _chatLines[_chatLines.Count - 1];
                tailBeforeMs = NormalizeUnixTimeMs(tailBefore?.UnixTimeMs ?? 0);
                tailBeforeId = tailBefore?.MessageId ?? string.Empty;
            }

            TavernChatLine line = new TavernChatLine
            {
                MessageId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId,
                PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Anonymous" : playerName,
                Text = (text ?? string.Empty).Trim(),
                UnixTimeMs = normalizedMs,
                IsSelf = isSelf,
            };

            int existingIndex = _chatLines.FindIndex(
                x => string.Equals(x?.MessageId, line.MessageId, StringComparison.Ordinal)
            );
            if (existingIndex >= 0)
            {
                _chatLines[existingIndex] = line;
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "AddChatLine update id="
                        + line.MessageId
                        + " idx="
                        + existingIndex
                        + " ms="
                        + normalizedMs
                        + " isSelf="
                        + isSelf
                        + " total="
                        + _chatLines.Count
                );
                NotifyStateChanged();
                return;
            }

            _chatLines.Add(line);
            int overflow = _chatLines.Count - MaxChatCache;
            if (overflow > 0)
            {
                _chatLines.RemoveRange(0, overflow);
            }

            long tailAfterMs = 0;
            string tailAfterId = string.Empty;
            if (_chatLines.Count > 0)
            {
                TavernChatLine tailAfter = _chatLines[_chatLines.Count - 1];
                tailAfterMs = NormalizeUnixTimeMs(tailAfter?.UnixTimeMs ?? 0);
                tailAfterId = tailAfter?.MessageId ?? string.Empty;
            }

            bool olderThanTailBefore = tailBeforeMs > 0 && normalizedMs < tailBeforeMs;
            CalradiaTavernDebug.Trace(
                "Behavior",
                "AddChatLine append id="
                    + line.MessageId
                    + " ms="
                    + normalizedMs
                    + " isSelf="
                    + isSelf
                    + " olderThanTailBefore="
                    + olderThanTailBefore
                    + " tailBeforeId="
                    + tailBeforeId
                    + " tailBeforeMs="
                    + tailBeforeMs
                    + " tailAfterId="
                    + tailAfterId
                    + " tailAfterMs="
                    + tailAfterMs
                    + " total="
                    + _chatLines.Count
                    + " overflowRemoved="
                    + Math.Max(0, overflow)
            );

            NotifyStateChanged();
        }

        private static void NotifyStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch
            {
                // Keep behavior robust even if a UI subscriber throws.
            }
        }

        private static bool IsEndpointNotFoundError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            string text = error.Trim();
            return text.IndexOf("(404)", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void TryUpsertPlayer(bool displayError, bool allowInactive = false)
        {
            if (!_presenceActive && !allowInactive)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_playerId) || string.IsNullOrWhiteSpace(_displayName))
            {
                return;
            }

            TavernUpsertPlayerRequest request = new TavernUpsertPlayerRequest
            {
                PlayerId = _playerId,
                PlayerName = _displayName,
                ChannelId = _channelId,
                ClientState = GetCurrentClientStateCode(),
                IsTavernActive = _presenceActive,
            };

            if (Interlocked.CompareExchange(ref _upsertInFlight, 1, 0) != 0)
            {
                return;
            }

            string serverUrl = _serverUrl;
            Task.Run(
                () =>
                {
                    try
                    {
                        bool ok;
                        string error;
                        lock (_netLock)
                        {
                            EnsureApi();
                            _api.BaseUrl = serverUrl;
                            ok = _api.UpsertPlayer(request, out TavernUpsertPlayerResponse _, out error);
                        }

                        EnqueueMainThreadAction(
                            () =>
                            {
                                try
                                {
                                    if (!ok && displayError)
                                    {
                                        InformationManager.DisplayMessage(
                                            new InformationMessage(
                                                "[Calradia Tavern] Connection failed: " + error,
                                                Colors.Red
                                            )
                                        );
                                    }
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref _upsertInFlight, 0);
                                }
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        EnqueueMainThreadAction(
                            () =>
                            {
                                CalradiaTavernDebug.ReportException("Behavior.TryUpsertPlayer", ex);
                                Interlocked.Exchange(ref _upsertInFlight, 0);
                            }
                        );
                    }
                }
            );
        }

        private void EnsureReady()
        {
            ApplyConfiguredServerUrl();
            ApplyConfiguredDisplayName();

            if (string.IsNullOrWhiteSpace(_playerId))
            {
                _playerId = Guid.NewGuid().ToString("N");
            }

            if (string.IsNullOrWhiteSpace(_displayName))
            {
                string heroName = Hero.MainHero?.Name?.ToString();
                _displayName = string.IsNullOrWhiteSpace(heroName) ? "Tavern Traveler" : heroName.Trim();
            }

            if (_displayName.Length > 20)
            {
                _displayName = _displayName.Substring(0, 20);
            }

            _channelId = FixedChannelId;
            _serverUrl = NormalizeServerUrl(_serverUrl) ?? DefaultServerUrl;
            _seenChatIds ??= new List<string>();
            _seenDeliveryIds ??= new List<string>();
            _chatLines ??= new List<TavernChatLine>();
            _onlinePlayerNames ??= new List<string>();
            _marketListings ??= new List<TavernMarketListing>();
            _onlinePlayerLastSeenUnixMs ??= new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase
            );
            EnsureApi();
            EnsureSessionCursorInitialized();
        }

        private void ApplyConfiguredServerUrl()
        {
            string configured = NormalizeServerUrl(CalradiaTavernSettings.Instance?.ServerUrl);
            string next = string.IsNullOrWhiteSpace(configured) ? DefaultServerUrl : configured;

            if (string.Equals(_serverUrl, next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _serverUrl = next;
            _giftRequestsEndpointUnavailable = false;
            _blockedPlayersEndpointUnavailable = false;
            if (_api != null)
            {
                _api.BaseUrl = _serverUrl;
            }
        }

        private void ApplyConfiguredDisplayName()
        {
            string configured = NormalizeDisplayName(CalradiaTavernSettings.Instance?.UserName);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return;
            }

            if (string.Equals(_displayName, configured, StringComparison.Ordinal))
            {
                return;
            }

            _displayName = configured;
            if (_presenceActive)
            {
                TryUpsertPlayer(false);
            }
            NotifyStateChanged();
        }

        private static string NormalizeServerUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string trimmed = raw.Trim().TrimEnd('/');
            bool valid =
                trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            return valid ? trimmed : null;
        }

        private static string NormalizeDisplayName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string trimmed = raw.Trim();
            if (trimmed.Length > 20)
            {
                trimmed = trimmed.Substring(0, 20);
            }

            return trimmed;
        }

        private void EnsureApi()
        {
            if (_api == null)
            {
                _api = new TavernApiClient(_serverUrl);
            }

            _api.BaseUrl = _serverUrl;
        }

        private static void RememberId(List<string> list, string id)
        {
            if (list == null || string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            list.Add(id);
            int overflow = list.Count - MaxSeenIdCache;
            if (overflow > 0)
            {
                list.RemoveRange(0, overflow);
            }
        }

        public static string GetMarketCategoryDisplayName(TavernMarketCategory category)
        {
            switch (category)
            {
                case TavernMarketCategory.Melee:
                    return "近战武器";
                case TavernMarketCategory.ShieldRanged:
                    return "盾牌远程";
                case TavernMarketCategory.Armor:
                    return "护甲";
                case TavernMarketCategory.Banner:
                    return "旗帜";
                default:
                    return "全部";
            }
        }

        private static string MarketCategoryToServerCode(TavernMarketCategory category)
        {
            switch (category)
            {
                case TavernMarketCategory.Melee:
                    return "Melee";
                case TavernMarketCategory.ShieldRanged:
                    return "ShieldRanged";
                case TavernMarketCategory.Armor:
                    return "Armor";
                case TavernMarketCategory.Banner:
                    return "Banner";
                default:
                    return "Unknown";
            }
        }

        private static TavernMarketCategory ParseMarketCategoryFromServerCode(string code)
        {
            string normalized = (code ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return TavernMarketCategory.All;
            }

            if (normalized.Equals("Melee", StringComparison.OrdinalIgnoreCase))
            {
                return TavernMarketCategory.Melee;
            }

            if (
                normalized.Equals("ShieldRanged", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("ShieldAndRanged", StringComparison.OrdinalIgnoreCase)
            )
            {
                return TavernMarketCategory.ShieldRanged;
            }

            if (normalized.Equals("Armor", StringComparison.OrdinalIgnoreCase))
            {
                return TavernMarketCategory.Armor;
            }

            if (normalized.Equals("Banner", StringComparison.OrdinalIgnoreCase))
            {
                return TavernMarketCategory.Banner;
            }

            // Compatibility with localized server strings.
            if (normalized.Contains("近战"))
            {
                return TavernMarketCategory.Melee;
            }
            if (normalized.Contains("盾") || normalized.Contains("远程"))
            {
                return TavernMarketCategory.ShieldRanged;
            }
            if (normalized.Contains("护甲"))
            {
                return TavernMarketCategory.Armor;
            }
            if (normalized.Contains("旗"))
            {
                return TavernMarketCategory.Banner;
            }

            return TavernMarketCategory.All;
        }

        private bool TryValidateDirectTradeItem(ItemObject item, out string error)
        {
            error = string.Empty;
            if (item == null)
            {
                error = "item not found.";
                return false;
            }

            string itemId = item.StringId ?? string.Empty;
            if (itemId.Length == 0)
            {
                error = "invalid item id.";
                return false;
            }

            if (!TryClassifyMarketCategory(item, out _))
            {
                error = "only weapons/armor/banners are tradable.";
                return false;
            }

            return true;
        }

        private bool TryValidateMarketItem(
            ItemObject item,
            out TavernMarketCategory category,
            out string error
        )
        {
            category = TavernMarketCategory.All;
            error = string.Empty;

            if (item == null)
            {
                error = "物品不存在。";
                return false;
            }

            string itemId = item.StringId ?? string.Empty;
            if (itemId.Length == 0)
            {
                error = "物品ID无效。";
                return false;
            }

            if (!IsNativeBaseGameItemId(itemId))
            {
                error = "只能上架原版物品。";
                return false;
            }

            if (!TryClassifyMarketCategory(item, out category))
            {
                error = "该物品不在允许的上架分类中（近战/盾牌远程/护甲/旗帜）。";
                return false;
            }

            return true;
        }

        private static bool TryClassifyMarketCategory(
            ItemObject item,
            out TavernMarketCategory category
        )
        {
            category = TavernMarketCategory.All;
            if (item == null)
            {
                return false;
            }

            switch (item.ItemType)
            {
                case ItemObject.ItemTypeEnum.OneHandedWeapon:
                case ItemObject.ItemTypeEnum.TwoHandedWeapon:
                case ItemObject.ItemTypeEnum.Polearm:
                    category = TavernMarketCategory.Melee;
                    return true;

                case ItemObject.ItemTypeEnum.Shield:
                case ItemObject.ItemTypeEnum.Bow:
                case ItemObject.ItemTypeEnum.Crossbow:
                case ItemObject.ItemTypeEnum.Thrown:
                case ItemObject.ItemTypeEnum.Arrows:
                case ItemObject.ItemTypeEnum.Bolts:
                    category = TavernMarketCategory.ShieldRanged;
                    return true;

                case ItemObject.ItemTypeEnum.HeadArmor:
                case ItemObject.ItemTypeEnum.BodyArmor:
                case ItemObject.ItemTypeEnum.LegArmor:
                case ItemObject.ItemTypeEnum.HandArmor:
                case ItemObject.ItemTypeEnum.Cape:
                    category = TavernMarketCategory.Armor;
                    return true;

                case ItemObject.ItemTypeEnum.Banner:
                    category = TavernMarketCategory.Banner;
                    return true;

                default:
                    break;
            }

            // Fallback by components for edge cases where ItemType enum is unusual.
            if (item.WeaponComponent != null)
            {
                category = TavernMarketCategory.Melee;
                return true;
            }

            if (item.ArmorComponent != null)
            {
                category = TavernMarketCategory.Armor;
                return true;
            }

            string id = item.StringId ?? string.Empty;
            if (id.IndexOf("banner", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                category = TavernMarketCategory.Banner;
                return true;
            }

            return false;
        }

        private bool IsNativeBaseGameItemId(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            EnsureNativeItemIdsLoaded();
            if (_nativeItemIds == null || _nativeItemIds.Count == 0)
            {
                return false;
            }

            return _nativeItemIds.Contains(itemId.Trim());
        }

        private void EnsureNativeItemIdsLoaded()
        {
            if (_nativeItemIdsLoaded)
            {
                return;
            }

            lock (_nativeItemIdsLock)
            {
                if (_nativeItemIdsLoaded)
                {
                    return;
                }

                HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string modulesRoot = Path.GetFullPath(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "Modules")
                    );
                    string[] nativeModules =
                    {
                        "Native",
                        "SandboxCore",
                        "Sandbox",
                        "StoryMode",
                        "CustomBattle",
                    };

                    foreach (string moduleName in nativeModules)
                    {
                        string moduleDataDir = Path.Combine(modulesRoot, moduleName, "ModuleData");
                        if (!Directory.Exists(moduleDataDir))
                        {
                            continue;
                        }

                        foreach (
                            string xmlPath in Directory.EnumerateFiles(
                                moduleDataDir,
                                "*.xml",
                                SearchOption.AllDirectories
                            )
                        )
                        {
                            TryCollectItemIdsFromXml(xmlPath, ids);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CalradiaTavernDebug.Trace(
                        "Behavior",
                        "EnsureNativeItemIdsLoaded failed: " + ex.Message
                    );
                }

                _nativeItemIds = ids;
                _nativeItemIdsLoaded = true;
                CalradiaTavernDebug.Trace("Behavior", "Native item cache loaded count=" + ids.Count);
            }
        }

        private static void TryCollectItemIdsFromXml(string xmlPath, HashSet<string> ids)
        {
            if (string.IsNullOrWhiteSpace(xmlPath) || ids == null)
            {
                return;
            }

            try
            {
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                };

                using (XmlReader reader = XmlReader.Create(xmlPath, settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            continue;
                        }

                        if (!string.Equals(reader.Name, "Item", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string id = reader.GetAttribute("id") ?? reader.GetAttribute("Id");
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        ids.Add(id.Trim());
                    }
                }
            }
            catch
            {
                // Ignore malformed or unrelated XML files.
            }
        }

        private static bool AreMarketListingsEqual(
            List<TavernMarketListing> left,
            List<TavernMarketListing> right
        )
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                TavernMarketListing l = left[i];
                TavernMarketListing r = right[i];
                if (l == null || r == null)
                {
                    return false;
                }

                if (
                    !string.Equals(l.ListingId, r.ListingId, StringComparison.Ordinal)
                    || !string.Equals(l.SellerPlayerId, r.SellerPlayerId, StringComparison.Ordinal)
                    || !string.Equals(l.ItemId, r.ItemId, StringComparison.Ordinal)
                    || l.ItemCount != r.ItemCount
                    || l.PriceDenars != r.PriceDenars
                    || l.Category != r.Category
                    || !string.Equals(l.Status, r.Status, StringComparison.OrdinalIgnoreCase)
                    || l.CreatedUnixTimeMs != r.CreatedUnixTimeMs
                    || l.PublicUnixTimeMs != r.PublicUnixTimeMs
                )
                {
                    return false;
                }
            }

            return true;
        }

        private static void ChangeMainHeroGold(int delta)
        {
            if (delta == 0 || Hero.MainHero == null)
            {
                return;
            }

            try
            {
                MethodInfo method = typeof(Hero).GetMethod(
                    "ChangeHeroGold",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(int) },
                    null
                );
                method?.Invoke(Hero.MainHero, new object[] { delta });
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.Trace("Behavior", "ChangeMainHeroGold failed: " + ex.Message);
            }
        }

        private bool TryApplyDeliveryPayload(
            TavernDeliveryDto delivery,
            out string itemName,
            out int displayCount
        )
        {
            itemName = string.Empty;
            displayCount = 0;
            if (delivery == null || delivery.Count <= 0)
            {
                return false;
            }

            if (
                string.Equals(
                    delivery.ItemId ?? string.Empty,
                    DenarDeliveryItemId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                ChangeMainHeroGold(delivery.Count);
                itemName = "第纳尔";
                displayCount = delivery.Count;
                return true;
            }

            if (!GiveItem(delivery.ItemId, delivery.Count))
            {
                ItemObject unresolved = FindItem(delivery.ItemId);
                int baseValue = Math.Max(1, unresolved?.Value ?? 50);
                int compensation = Math.Max(1, baseValue * delivery.Count / 5);
                ChangeMainHeroGold(compensation);
                itemName = "未知物品（已折算）";
                displayCount = compensation;
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "ApplyDelivery fallback itemId="
                        + (delivery.ItemId ?? string.Empty)
                        + " count="
                        + delivery.Count
                        + " compensation="
                        + compensation
                );
                return true;
            }

            itemName = ResolveItemName(delivery.ItemId);
            displayCount = delivery.Count;
            return true;
        }

        private bool TryTakeItem(string itemId, int count, out ItemObject item, out string error)
        {
            item = null;
            error = string.Empty;

            if (count <= 0)
            {
                error = "Count must be > 0.";
                return false;
            }

            item = FindItem(itemId);
            if (item == null)
            {
                error = "Item not found: " + itemId;
                return false;
            }

            ItemRoster roster = MobileParty.MainParty?.ItemRoster;
            if (roster == null)
            {
                error = "Main party inventory is not available.";
                return false;
            }

            int idx = roster.FindIndexOfItem(item);
            int have = idx >= 0 ? Math.Max(0, roster.GetElementNumber(idx)) : 0;
            if (have < count)
            {
                error = "Not enough items. Have " + have + ", need " + count + ".";
                return false;
            }

            roster.AddToCounts(item, -count);
            return true;
        }

        private bool GiveItem(string itemId, int count)
        {
            if (count <= 0)
            {
                return false;
            }

            ItemObject item = FindItem(itemId);
            ItemRoster roster = MobileParty.MainParty?.ItemRoster;
            if (item == null || roster == null)
            {
                return false;
            }

            roster.AddToCounts(item, count);
            return true;
        }

        private string ResolveItemName(string itemId)
        {
            ItemObject item = FindItem(itemId);
            return item?.Name?.ToString() ?? itemId;
        }

        private static ItemObject FindItem(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            return Game.Current?.ObjectManager?.GetObject<ItemObject>(itemId.Trim());
        }

        private static long NormalizeUnixTimeMs(long value)
        {
            if (value <= 0)
            {
                return 0;
            }

            long normalized = value;

            // 10-digit Unix seconds -> milliseconds.
            if (normalized < 100_000_000_000L)
            {
                normalized *= 1000L;
            }
            // 16-digit Unix microseconds -> milliseconds.
            else if (normalized > 100_000_000_000_000L)
            {
                normalized /= 1000L;
            }

            return normalized;
        }

        private static bool IsMarketListingExpired(TavernMarketListing listing, long nowMs)
        {
            if (listing == null)
            {
                return true;
            }

            long createdMs = NormalizeUnixTimeMs(listing.CreatedUnixTimeMs);
            if (createdMs <= 0)
            {
                return false;
            }

            long expireMs = createdMs + (long)MarketListingLifetimeSeconds * 1000L;
            return nowMs >= expireMs;
        }

        private static bool AreOnlinePresenceEqual(
            Dictionary<string, long> left,
            Dictionary<string, long> right
        )
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, long> kv in left)
            {
                if (!right.TryGetValue(kv.Key, out long rightValue))
                {
                    return false;
                }

                if (kv.Value != rightValue)
                {
                    return false;
                }
            }

            return true;
        }

        private static string FormatUtcOffset(TimeSpan offset)
        {
            int totalMinutes = (int)offset.TotalMinutes;
            string sign = totalMinutes >= 0 ? "+" : "-";
            int absMinutes = Math.Abs(totalMinutes);
            int hours = absMinutes / 60;
            int minutes = absMinutes % 60;

            if (minutes == 0)
            {
                return "UTC" + sign + hours.ToString(CultureInfo.InvariantCulture);
            }

            return "UTC"
                + sign
                + hours.ToString(CultureInfo.InvariantCulture)
                + ":"
                + minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            try
            {
                starter.AddGameMenuOption(
                    "town_backstreet",
                    "ctavern_open_intel_backstreet",
                    "\u4ea4\u6d41\u5361\u62c9\u8fea\u4e9a\u60c5\u62a5",
                    GameMenuOpenCondition,
                    GameMenuOpenConsequence,
                    false,
                    2,
                    false
                );
                CalradiaTavernDebug.Trace("Behavior", "Menu option registered: town_backstreet");
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("Behavior.OnSessionLaunched.AddGameMenuOption", ex);
            }
        }

        private static bool GameMenuOpenCondition(MenuCallbackArgs args)
        {
            if (args != null)
            {
                args.optionLeaveType = GameMenuOption.LeaveType.Submenu;
                args.IsEnabled = true;
            }
            return true;
        }

        private static void GameMenuOpenConsequence(MenuCallbackArgs args)
        {
            try
            {
                long started = CalradiaTavernDebug.NowMs;
                CalradiaTavernDebug.Trace("Behavior", "GameMenuOpenConsequence begin");
                CalradiaTavern.UI.CalradiaTavernScreenManager.Open();
                CalradiaTavernDebug.Trace(
                    "Behavior",
                    "GameMenuOpenConsequence end elapsedMs=" + (CalradiaTavernDebug.NowMs - started)
                );
            }
            catch (Exception ex)
            {
                CalradiaTavernDebug.ReportException("Behavior.GameMenuOpenConsequence", ex);
            }
        }

        private void EnsureSessionCursorInitialized()
        {
            if (_sessionCursorInitialized)
            {
                return;
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _lastChatUnixMs = nowMs;
            _seenChatIds?.Clear();
            _chatLines?.Clear();
            _marketListings?.Clear();
            _sessionCursorInitialized = true;
            CalradiaTavernDebug.Trace("Behavior", "Session chat cursor initialized at " + nowMs);
        }
    }
}



using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CalradiaTavern.Models;
using CalradiaTavern.Networking;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;

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

    public sealed class CalradiaTavernCampaignBehavior : CampaignBehaviorBase
    {
        private const int MaxChatLength = 180;
        private const int MaxSeenIdCache = 1000;
        private const int MaxChatCache = 180;
        private const float PollIntervalSeconds = 1.2f;
        private const float RegisterIntervalSeconds = 60f;
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
        private int _unreadChatCount;
        private float _pollElapsed;
        private float _registerElapsed;
        private TavernApiClient _api;
        private bool _sessionCursorInitialized;

        public static event Action StateChanged;

        public static CalradiaTavernCampaignBehavior Instance =>
            Campaign.Current?.GetCampaignBehavior<CalradiaTavernCampaignBehavior>();

        public int UnreadChatCount => Math.Max(0, _unreadChatCount);

        public string DisplayName => _displayName;

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

            _playerId ??= string.Empty;
            _displayName ??= string.Empty;
            _channelId = FixedChannelId;
            _serverUrl = NormalizeServerUrl(_serverUrl) ?? DefaultServerUrl;
            _seenChatIds ??= new List<string>();
            _seenDeliveryIds ??= new List<string>();
            _chatLines ??= new List<TavernChatLine>();
            _unreadChatCount = Math.Max(0, _unreadChatCount);
        }

        public string PullNow()
        {
            EnsureReady();

            int chatCount = PullChat();
            int deliveryCount = PullDeliveries();
            return $"Refreshed: {chatCount} new chat message(s), {deliveryCount} new delivery(ies).";
        }

        public IReadOnlyList<TavernChatLine> GetRecentChatLines(int maxCount = 120)
        {
            EnsureReady();
            int take = Math.Max(1, Math.Min(300, maxCount));
            int skip = Math.Max(0, _chatLines.Count - take);
            return _chatLines.Skip(skip).ToList();
        }
        public IReadOnlyList<string> GetKnownPlayers(int maxCount = 80)
        {
            EnsureReady();
            int take = Math.Max(1, Math.Min(200, maxCount));
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string self = string.IsNullOrWhiteSpace(_displayName) ? "Me" : _displayName.Trim();
            if (seen.Add(self))
            {
                names.Add(self);
            }
            for (int i = _chatLines.Count - 1; i >= 0 && names.Count < take; i--)
            {
                string candidate = _chatLines[i]?.PlayerName;
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }
                string name = candidate.Trim();
                if (!seen.Add(name))
                {
                    continue;
                }
                names.Add(name);
            }
            return names;
        }

        public void MarkChatRead()
        {
            _unreadChatCount = 0;
            NotifyStateChanged();
        }

        public string SendChat(string rawText)
        {
            EnsureReady();

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

            if (!_api.SendChat(request, out TavernSendChatResponse response, out string error))
            {
                return "Send failed: " + error;
            }

            if (response != null)
            {
                _lastChatUnixMs = Math.Max(_lastChatUnixMs, response.UnixTimeMs);
                RememberId(_seenChatIds, response.MessageId);
                AddChatLine(
                    response.MessageId,
                    selfName,
                    text,
                    response.UnixTimeMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : response.UnixTimeMs,
                    true
                );
            }
            else
            {
                AddChatLine(
                    Guid.NewGuid().ToString("N"),
                    selfName,
                    text,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    true
                );
            }

            string preview = text.Length > 24 ? text.Substring(0, 24) + "..." : text;
            if (response != null && !string.IsNullOrWhiteSpace(response.MessageId))
            {
                string shortId = response.MessageId.Length > 8
                    ? response.MessageId.Substring(0, 8)
                    : response.MessageId;
                return selfName + " sent #" + shortId + ": " + preview;
            }

            return selfName + " sent: " + preview;
        }

        public static string FormatChatToast(string playerName, string text, long unixTimeMs)
        {
            string sender = string.IsNullOrWhiteSpace(playerName) ? "Anonymous" : playerName.Trim();
            string body = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
            DateTimeOffset local = unixTimeMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(unixTimeMs).ToLocalTime()
                : DateTimeOffset.Now;
            string offset = FormatUtcOffset(local.Offset);
            return "["
                + offset
                + " | "
                + local.ToString("HH:mm", CultureInfo.InvariantCulture)
                + "] "
                + sender
                + ": "
                + body;
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

        public string SendItemToPlayer(string targetPlayerName, string itemId, int count)
        {
            EnsureReady();

            string target = (targetPlayerName ?? string.Empty).Trim();
            if (target.Length < 2)
            {
                return "Target player name must be at least 2 characters.";
            }

            if (string.Equals(target, _displayName, StringComparison.OrdinalIgnoreCase))
            {
                return "Cannot send to yourself.";
            }

            if (count <= 0)
            {
                return "Count must be greater than 0.";
            }

            if (!TryTakeItem(itemId, count, out ItemObject item, out string takeError))
            {
                return "Send failed: " + takeError;
            }

            TavernDirectSendRequest request = new TavernDirectSendRequest
            {
                ChannelId = _channelId,
                FromPlayerId = _playerId,
                FromPlayerName = _displayName,
                TargetPlayerName = target,
                ItemId = item.StringId,
                Count = count,
                ClientNonce = Guid.NewGuid().ToString("N"),
            };

            if (!_api.SendDirectItem(request, out TavernDirectSendResponse response, out string error))
            {
                GiveItem(item.StringId, count);
                return "Send failed: " + error;
            }

            InformationManager.DisplayMessage(
                new InformationMessage(
                    "[Calradia Tavern] Sent "
                        + count
                        + "x "
                        + (item.Name?.ToString() ?? item.StringId)
                        + " to "
                        + (response?.TargetPlayerName ?? target)
                        + ".",
                    Colors.Green
                )
            );

            return "Sent.";
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
            EnsureReady();
            ApplyConfiguredServerUrl();
            ApplyConfiguredDisplayName();

            _pollElapsed += Math.Max(0f, dt);
            _registerElapsed += Math.Max(0f, dt);

            if (_registerElapsed >= RegisterIntervalSeconds)
            {
                _registerElapsed = 0f;
                TryUpsertPlayer(false);
            }

            if (_pollElapsed < PollIntervalSeconds)
            {
                return;
            }

            _pollElapsed = 0f;
            PullChat();
            PullDeliveries();
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

            int count = 0;
            foreach (TavernChatMessageDto msg in messages.OrderBy(x => x.UnixTimeMs))
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.MessageId))
                {
                    continue;
                }

                _lastChatUnixMs = Math.Max(_lastChatUnixMs, msg.UnixTimeMs);
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
                AddChatLine(
                    msg.MessageId,
                    senderName,
                    msg.Text ?? string.Empty,
                    msg.UnixTimeMs,
                    isSelf
                );

                if (!isSelf)
                {
                    _unreadChatCount++;
                    InformationManager.DisplayMessage(
                        new InformationMessage(
                            FormatChatToast(senderName, msg.Text ?? string.Empty, msg.UnixTimeMs),
                            Colors.Cyan
                        )
                    );
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
            foreach (TavernDeliveryDto delivery in deliveries.OrderBy(x => x.UnixTimeMs))
            {
                if (delivery == null || string.IsNullOrWhiteSpace(delivery.DeliveryId))
                {
                    continue;
                }

                _lastDeliveryUnixMs = Math.Max(_lastDeliveryUnixMs, delivery.UnixTimeMs);
                if (_seenDeliveryIds.Contains(delivery.DeliveryId))
                {
                    continue;
                }

                RememberId(_seenDeliveryIds, delivery.DeliveryId);
                if (delivery.Count <= 0 || !GiveItem(delivery.ItemId, delivery.Count))
                {
                    continue;
                }

                string itemName = ResolveItemName(delivery.ItemId);
                string fromName = string.IsNullOrWhiteSpace(delivery.FromPlayerName)
                    ? "Anonymous"
                    : delivery.FromPlayerName;

                InformationManager.DisplayMessage(
                    new InformationMessage(
                        "[Calradia Tavern] Received "
                            + delivery.Count
                            + "x "
                            + itemName
                            + " from "
                            + fromName
                            + ".",
                        Colors.Green
                    )
                );
                AddChatLine(
                    "sys_" + delivery.DeliveryId,
                    "System",
                    "Received " + delivery.Count + "x " + itemName + " from " + fromName,
                    delivery.UnixTimeMs,
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

        private void AddChatLine(string messageId, string playerName, string text, long unixMs, bool isSelf)
        {
            TavernChatLine line = new TavernChatLine
            {
                MessageId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId,
                PlayerName = string.IsNullOrWhiteSpace(playerName) ? "Anonymous" : playerName,
                Text = (text ?? string.Empty).Trim(),
                UnixTimeMs = unixMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : unixMs,
                IsSelf = isSelf,
            };

            _chatLines.Add(line);
            int overflow = _chatLines.Count - MaxChatCache;
            if (overflow > 0)
            {
                _chatLines.RemoveRange(0, overflow);
            }

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

        private void TryUpsertPlayer(bool displayError)
        {
            if (string.IsNullOrWhiteSpace(_playerId) || string.IsNullOrWhiteSpace(_displayName))
            {
                return;
            }

            _api ??= new TavernApiClient(_serverUrl);
            _api.BaseUrl = _serverUrl;

            TavernUpsertPlayerRequest request = new TavernUpsertPlayerRequest
            {
                PlayerId = _playerId,
                PlayerName = _displayName,
                ChannelId = _channelId,
            };

            if (!_api.UpsertPlayer(request, out TavernUpsertPlayerResponse _, out string error) && displayError)
            {
                InformationManager.DisplayMessage(
                    new InformationMessage("[Calradia Tavern] Connection failed: " + error, Colors.Red)
                );
            }
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
            TryUpsertPlayer(false);
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
                TryUpsertPlayer(false);
                return;
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
                CalradiaTavern.UI.CalradiaTavernScreenManager.Open();
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
            _sessionCursorInitialized = true;
            CalradiaTavernDebug.Trace("Behavior", "Session chat cursor initialized at " + nowMs);
        }
    }
}


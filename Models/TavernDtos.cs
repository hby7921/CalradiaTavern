namespace CalradiaTavern.Models
{
    public sealed class TavernApiEnvelope<T>
    {
        public bool Ok { get; set; }
        public string Error { get; set; }
        public T Data { get; set; }
    }

    public sealed class TavernChatMessageDto
    {
        public string MessageId { get; set; }
        public string ChannelId { get; set; }
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public string Text { get; set; }
        public long UnixTimeMs { get; set; }
    }

    public sealed class TavernSendChatRequest
    {
        public string ChannelId { get; set; }
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public string Text { get; set; }
        public string ClientNonce { get; set; }
    }

    public sealed class TavernSendChatResponse
    {
        public string MessageId { get; set; }
        public long UnixTimeMs { get; set; }
    }

    public sealed class TavernUpsertPlayerRequest
    {
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public string ChannelId { get; set; }
    }

    public sealed class TavernUpsertPlayerResponse
    {
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public long UnixTimeMs { get; set; }
    }

    public sealed class TavernDirectSendRequest
    {
        public string ChannelId { get; set; }
        public string FromPlayerId { get; set; }
        public string FromPlayerName { get; set; }
        public string TargetPlayerName { get; set; }
        public string ItemId { get; set; }
        public int Count { get; set; }
        public string ClientNonce { get; set; }
    }

    public sealed class TavernDirectSendResponse
    {
        public string DeliveryId { get; set; }
        public string TargetPlayerId { get; set; }
        public string TargetPlayerName { get; set; }
        public long UnixTimeMs { get; set; }
    }

    public sealed class TavernDeliveryDto
    {
        public string DeliveryId { get; set; }
        public string ChannelId { get; set; }
        public string PlayerId { get; set; }
        public string FromPlayerId { get; set; }
        public string FromPlayerName { get; set; }
        public string ItemId { get; set; }
        public int Count { get; set; }
        public string Note { get; set; }
        public long UnixTimeMs { get; set; }
    }

    // Legacy offer-based trading DTOs remain for console compatibility.
    public sealed class TavernTradeOfferDto
    {
        public string OfferId { get; set; }
        public string ChannelId { get; set; }
        public string SellerPlayerId { get; set; }
        public string SellerName { get; set; }
        public string GiveItemId { get; set; }
        public int GiveItemCount { get; set; }
        public string WantItemId { get; set; }
        public int WantItemCount { get; set; }
        public string Status { get; set; }
        public long UnixTimeMs { get; set; }
    }

    public sealed class TavernPublishOfferRequest
    {
        public string ChannelId { get; set; }
        public string SellerPlayerId { get; set; }
        public string SellerName { get; set; }
        public string GiveItemId { get; set; }
        public int GiveItemCount { get; set; }
        public string WantItemId { get; set; }
        public int WantItemCount { get; set; }
        public string ClientNonce { get; set; }
    }

    public sealed class TavernPublishOfferResponse
    {
        public string OfferId { get; set; }
        public long UnixTimeMs { get; set; }
    }

    public sealed class TavernAcceptOfferRequest
    {
        public string OfferId { get; set; }
        public string BuyerPlayerId { get; set; }
        public string BuyerName { get; set; }
        public string ClientNonce { get; set; }
    }

    public sealed class TavernAcceptOfferResponse
    {
        public string OfferId { get; set; }
        public string GrantedItemId { get; set; }
        public int GrantedItemCount { get; set; }
        public long UnixTimeMs { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CalradiaTavern.Models;

namespace CalradiaTavern.Networking
{
    public sealed class TavernApiClient
    {
        private const int RequestTimeoutMs = 12000;
        private const int GetRetryCount = 2;
        private const int PostRetryCount = 1;
        private const int RetryBaseDelayMs = 300;

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
        };

        private string _baseUrl;

        public TavernApiClient(string baseUrl)
        {
            _baseUrl = NormalizeBaseUrl(baseUrl);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set => _baseUrl = NormalizeBaseUrl(value);
        }

        public bool UpsertPlayer(
            TavernUpsertPlayerRequest request,
            out TavernUpsertPlayerResponse data,
            out string error
        )
        {
            return Post("/api/v1/player/upsert", request, out data, out error);
        }

        public bool ListPlayers(
            string channelId,
            int activeWithinSec,
            int limit,
            out List<TavernPlayerPresenceDto> data,
            out string error
        )
        {
            int safeActiveWithinSec = Math.Max(5, Math.Min(1800, activeWithinSec));
            int safeLimit = Math.Max(1, Math.Min(300, limit));
            string path =
                "/api/v1/player/list?channelId="
                + Url(channelId)
                + "&activeWithinSec="
                + safeActiveWithinSec
                + "&limit="
                + safeLimit;
            return Get(path, out data, out error);
        }

        public bool BlockPlayer(
            TavernBlockPlayerRequest request,
            out TavernBlockPlayerResponse data,
            out string error
        )
        {
            return Post("/api/v1/player/block", request, out data, out error);
        }

        public bool ListBlockedPlayers(
            string channelId,
            string blockerPlayerId,
            out List<TavernBlockedPlayerDto> data,
            out string error
        )
        {
            string query =
                "?channelId=" + Url(channelId) + "&blockerPlayerId=" + Url(blockerPlayerId);
            string[] candidatePaths =
            {
                "/api/v1/player/blocked" + query,
                "/api/v1/player/block/list" + query,
                "/api/v1/player/blacklist" + query,
            };
            return GetWithFallback(candidatePaths, out data, out error);
        }

        public bool SendChat(TavernSendChatRequest request, out TavernSendChatResponse data, out string error)
        {
            return Post("/api/v1/chat/send", request, out data, out error);
        }

        public bool PullChat(
            string channelId,
            long afterUnixTimeMs,
            out List<TavernChatMessageDto> data,
            out string error
        )
        {
            string path =
                "/api/v1/chat/pull?channelId="
                + Url(channelId)
                + "&afterUnixMs="
                + Math.Max(0, afterUnixTimeMs);
            return Get(path, out data, out error);
        }

        public bool SendDirectItem(
            TavernDirectSendRequest request,
            out TavernDirectSendResponse data,
            out string error
        )
        {
            return Post("/api/v1/trade/direct_send", request, out data, out error);
        }

        public bool CreateGiftRequest(
            TavernGiftRequestCreateRequest request,
            out TavernGiftRequestCreateResponse data,
            out string error
        )
        {
            return Post("/api/v1/trade/gift_request/create", request, out data, out error);
        }

        public bool PullGiftRequests(
            string playerId,
            out List<TavernGiftRequestDto> data,
            out string error
        )
        {
            string query = "?playerId=" + Url(playerId);
            string[] candidatePaths =
            {
                "/api/v1/trade/gift_requests" + query,
                "/api/v1/trade/gift_request/pull" + query,
                "/api/v1/trade/gift/pull" + query,
            };
            return GetWithFallback(candidatePaths, out data, out error);
        }

        public bool RespondGiftRequest(
            TavernGiftRequestRespondRequest request,
            out TavernGiftRequestRespondResponse data,
            out string error
        )
        {
            return Post("/api/v1/trade/gift_request/respond", request, out data, out error);
        }

        public bool PullDeliveries(
            string playerId,
            long afterUnixTimeMs,
            out List<TavernDeliveryDto> data,
            out string error
        )
        {
            string path =
                "/api/v1/trade/deliveries?playerId="
                + Url(playerId)
                + "&afterUnixMs="
                + Math.Max(0, afterUnixTimeMs);
            return Get(path, out data, out error);
        }

        public bool PublishOffer(
            TavernPublishOfferRequest request,
            out TavernPublishOfferResponse data,
            out string error
        )
        {
            return Post("/api/v1/trade/publish", request, out data, out error);
        }

        public bool ListOffers(string channelId, out List<TavernTradeOfferDto> data, out string error)
        {
            string path = "/api/v1/trade/list?channelId=" + Url(channelId);
            return Get(path, out data, out error);
        }

        public bool AcceptOffer(
            TavernAcceptOfferRequest request,
            out TavernAcceptOfferResponse data,
            out string error
        )
        {
            return Post("/api/v1/trade/accept", request, out data, out error);
        }

        public bool PublishMarketListing(
            TavernMarketPublishRequest request,
            out TavernMarketPublishResponse data,
            out string error
        )
        {
            return Post("/api/v1/market/publish", request, out data, out error);
        }

        public bool ListMarketListings(
            string channelId,
            out List<TavernMarketListingDto> data,
            out string error
        )
        {
            string path = "/api/v1/market/list?channelId=" + Url(channelId);
            return Get(path, out data, out error);
        }

        public bool CancelMarketListing(
            TavernMarketCancelRequest request,
            out TavernMarketCancelResponse data,
            out string error
        )
        {
            return Post("/api/v1/market/cancel", request, out data, out error);
        }

        public bool BuyMarketListing(
            TavernMarketBuyRequest request,
            out TavernMarketBuyResponse data,
            out string error
        )
        {
            return Post("/api/v1/market/buy", request, out data, out error);
        }

        private bool Get<T>(string path, out T data, out string error)
        {
            return GetSinglePath(path, out data, out error);
        }

        private bool GetWithFallback<T>(IEnumerable<string> candidatePaths, out T data, out string error)
        {
            data = default;
            error = string.Empty;
            if (candidatePaths == null)
            {
                error = "No request path provided.";
                return false;
            }

            string firstError = string.Empty;
            string lastError = string.Empty;
            int attempted = 0;
            foreach (string rawPath in candidatePaths)
            {
                string path = rawPath ?? string.Empty;
                if (path.Length == 0)
                {
                    continue;
                }

                attempted++;
                if (GetSinglePath(path, out data, out string pathError))
                {
                    return true;
                }

                if (string.IsNullOrWhiteSpace(firstError))
                {
                    firstError = pathError;
                }
                lastError = pathError;
                if (!IsEndpointNotFound(pathError))
                {
                    error = pathError;
                    return false;
                }
            }

            error = attempted <= 0
                ? "No valid request path provided."
                : "All compatible endpoints failed. first="
                    + (firstError ?? string.Empty)
                    + " last="
                    + (lastError ?? string.Empty);
            return false;
        }

        private bool GetSinglePath<T>(string path, out T data, out string error)
        {
            data = default;
            error = string.Empty;

            string url = BuildUrl(path);
            for (int attempt = 0; attempt <= GetRetryCount; attempt++)
            {
                try
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";
                    request.Timeout = RequestTimeoutMs;
                    request.ReadWriteTimeout = RequestTimeoutMs;
                    request.ContentType = "application/json; charset=utf-8";

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (
                        StreamReader reader = new StreamReader(
                            response.GetResponseStream(),
                            Encoding.UTF8
                        )
                    )
                    {
                        string body = reader.ReadToEnd();
                        return TryParseEnvelope(body, out data, out error);
                    }
                }
                catch (WebException ex)
                {
                    error = BuildWebExceptionError("GET", path, ex, attempt, GetRetryCount);
                    if (attempt < GetRetryCount && ShouldRetry(ex))
                    {
                        Thread.Sleep(GetRetryDelayMs(attempt));
                        continue;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    error = "Unexpected error [GET " + path + "]: " + ex.Message;
                    return false;
                }
            }

            error = "Network error [GET " + path + "]: exhausted retries.";
            return false;
        }

        private bool Post<TRequest, TResponse>(
            string path,
            TRequest payload,
            out TResponse data,
            out string error
        )
        {
            data = default;
            error = string.Empty;

            string url = BuildUrl(path);
            for (int attempt = 0; attempt <= PostRetryCount; attempt++)
            {
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(_json.Serialize(payload));
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "POST";
                    request.Timeout = RequestTimeoutMs;
                    request.ReadWriteTimeout = RequestTimeoutMs;
                    request.ContentType = "application/json; charset=utf-8";
                    request.ContentLength = bytes.Length;

                    using (Stream reqStream = request.GetRequestStream())
                    {
                        reqStream.Write(bytes, 0, bytes.Length);
                    }

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (
                        StreamReader reader = new StreamReader(
                            response.GetResponseStream(),
                            Encoding.UTF8
                        )
                    )
                    {
                        string body = reader.ReadToEnd();
                        return TryParseEnvelope(body, out data, out error);
                    }
                }
                catch (WebException ex)
                {
                    error = BuildWebExceptionError("POST", path, ex, attempt, PostRetryCount);
                    if (attempt < PostRetryCount && ShouldRetry(ex))
                    {
                        Thread.Sleep(GetRetryDelayMs(attempt));
                        continue;
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    error = "Unexpected error [POST " + path + "]: " + ex.Message;
                    return false;
                }
            }

            error = "Network error [POST " + path + "]: exhausted retries.";
            return false;
        }

        private bool TryParseEnvelope<T>(string json, out T data, out string error)
        {
            data = default;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Empty response.";
                return false;
            }

            TavernApiEnvelope<T> envelope = _json.Deserialize<TavernApiEnvelope<T>>(json);
            if (envelope == null)
            {
                error = "Invalid response.";
                return false;
            }

            if (!envelope.Ok)
            {
                error = string.IsNullOrWhiteSpace(envelope.Error) ? "Server rejected request." : envelope.Error;
                return false;
            }

            data = envelope.Data;
            return true;
        }

        private string BuildUrl(string path)
        {
            string basePart = _baseUrl.TrimEnd('/');
            string pathPart = path.StartsWith("/") ? path : "/" + path;
            return basePart + pathPart;
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "http://127.0.0.1:18080";
            }

            return baseUrl.Trim().TrimEnd('/');
        }

        private static string Url(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        private static string TryReadErrorBody(WebException ex)
        {
            if (ex?.Response == null)
            {
                return string.Empty;
            }

            try
            {
                using (Stream stream = ex.Response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return string.Empty;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string body = reader.ReadToEnd();
                        if (string.IsNullOrWhiteSpace(body))
                        {
                            return string.Empty;
                        }

                        string trimmed = body.Trim();
                        if (trimmed.Length > 160)
                        {
                            trimmed = trimmed.Substring(0, 160) + "...";
                        }

                        return " | Response: " + trimmed;
                    }
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool ShouldRetry(WebException ex)
        {
            if (ex == null)
            {
                return false;
            }

            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout:
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                    return true;
            }

            HttpStatusCode? statusCode = GetHttpStatusCode(ex);
            if (statusCode.HasValue)
            {
                int code = (int)statusCode.Value;
                if (code == 429 || code == 502 || code == 503 || code == 504)
                {
                    return true;
                }
            }

            string message = ex.Message ?? string.Empty;
            return message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HttpStatusCode? GetHttpStatusCode(WebException ex)
        {
            HttpWebResponse response = ex?.Response as HttpWebResponse;
            if (response == null)
            {
                return null;
            }

            return response.StatusCode;
        }

        private static bool IsEndpointNotFound(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            return error.IndexOf("(404)", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetRetryDelayMs(int attempt)
        {
            int safeAttempt = Math.Max(0, attempt);
            return RetryBaseDelayMs * (safeAttempt + 1);
        }

        private static string BuildWebExceptionError(
            string method,
            string path,
            WebException ex,
            int attempt,
            int maxRetries
        )
        {
            HttpStatusCode? statusCode = GetHttpStatusCode(ex);
            string statusText = statusCode.HasValue ? ((int)statusCode.Value).ToString() : "n/a";
            string body = TryReadErrorBody(ex);
            return "Network error ["
                + method
                + " "
                + path
                + " attempt "
                + (attempt + 1)
                + "/"
                + (maxRetries + 1)
                + " status="
                + statusText
                + "]: "
                + ex.Message
                + body;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using CalradiaTavern.Models;

namespace CalradiaTavern.Networking
{
    public sealed class TavernApiClient
    {
        private const int RequestTimeoutMs = 5000;

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

        private bool Get<T>(string path, out T data, out string error)
        {
            data = default;
            error = string.Empty;

            string url = BuildUrl(path);
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = RequestTimeoutMs;
                request.ReadWriteTimeout = RequestTimeoutMs;
                request.ContentType = "application/json; charset=utf-8";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    return TryParseEnvelope(body, out data, out error);
                }
            }
            catch (WebException ex)
            {
                error = "Network error: " + ex.Message + TryReadErrorBody(ex);
                return false;
            }
            catch (Exception ex)
            {
                error = "Unexpected error: " + ex.Message;
                return false;
            }
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
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    return TryParseEnvelope(body, out data, out error);
                }
            }
            catch (WebException ex)
            {
                error = "Network error: " + ex.Message + TryReadErrorBody(ex);
                return false;
            }
            catch (Exception ex)
            {
                error = "Unexpected error: " + ex.Message;
                return false;
            }
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
    }
}

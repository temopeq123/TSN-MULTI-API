using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace TSN_MULTI_API.Core;

public class AuthManager
{
    private readonly HttpClient _httpClient;
    private const string EsiaBaseUrl = "https://esia-portal1.test.gosuslugi.ru";
    private string _lastPayloadDiagnostics = string.Empty;

    public AuthManager()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
    }

    public async Task<string> GetAccessTokenAsync(string apiKey, string certificateThumbprint)
    {
        string cleanApiKey = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(cleanApiKey))
            throw new ArgumentException("API-Key не указан.", nameof(apiKey));

        // Согласно ЕСИА подписывается именно UUID/API-Key, переданный в URL.
        byte[] payloadBytes = Encoding.UTF8.GetBytes(cleanApiKey);

        // Защита от скрытых символов/не того UUID: endpoint и signed payload
        // должны использовать абсолютно одинаковую строку API-Key.
        string payloadSha256 = Convert.ToHexString(SHA256.HashData(payloadBytes));
        byte[] signatureBytes = CryptoSigner.SignDetached(payloadBytes, certificateThumbprint);

        // ЕСИА ожидает urlSafeBase64. WebUtility.UrlEncode корректно кодирует +, / и =.
        // Перед этим убираем Base64 padding, как в примерах CryptoPro.
        string signatureUrlEncoded = CryptoSigner.ToEsiaSignatureQueryValue(signatureBytes);

        string requestUrl =
            $"{EsiaBaseUrl}/esia-rs/api/public/v1/orgs/ext-app/{cleanApiKey}/tkn" +
            $"?signature={signatureUrlEncoded}";

        // Логировать сам API-Key или подпись нельзя. SHA-256 нужен только для диагностики:
        // если сервер отвергнет подпись, можно проверить, что подписывался тот же UUID.
        _lastPayloadDiagnostics = $"API-Key UTF-8 bytes={payloadBytes.Length}, SHA256={payloadSha256}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("TSN-MULTI-API/1.0");

        using HttpResponseMessage response = await _httpClient.SendAsync(request);
        string responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(
                $"Ошибка API ЕСИА: {(int)response.StatusCode} {response.StatusCode}. " +
                $"Ответ: {responseJson}");
        }

        using JsonDocument jsonDoc = JsonDocument.Parse(responseJson);
        if (jsonDoc.RootElement.TryGetProperty("accessTkn", out JsonElement tokenElement))
        {
            string? token = tokenElement.GetString();
            if (!string.IsNullOrWhiteSpace(token))
                return token;
        }

        throw new Exception($"В ответе ЕСИА отсутствует непустое поле accessTkn. Ответ: {responseJson}");
    }
}

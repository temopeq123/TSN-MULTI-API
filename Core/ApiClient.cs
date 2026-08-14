using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TSN_MULTI_API.Core
{
    public class ApiClient
    {
        private readonly HttpClient _httpClient;
        private const string ApiEpguBaseUrl = "https://svcdev-beta.test.gosuslugi.ru/api/gusmev/";

        public ApiClient()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        }

        public async Task<long> SendPushAsync(byte[] archiveBytes, string accessToken)
        {
            string fullUrl = new Uri(new Uri(ApiEpguBaseUrl), "push").ToString();
            using var msg = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var content = new MultipartFormDataContent();
            string metaJson = "{\"region\":\"00000000000\", \"serviceCode\":\"10000000374\", \"targetCode\":\"-10000000374\"}";
            content.Add(new StringContent(metaJson, Encoding.UTF8, "application/json"), "meta");

            var fileContent = new ByteArrayContent(archiveBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/zip");
            content.Add(fileContent, "file", "archive.zip");

            msg.Content = content;

            HttpResponseMessage response = await _httpClient.SendAsync(msg);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Ошибка API ЕПГУ (Push): {response.StatusCode}. Ответ: {responseJson}");

            using JsonDocument jsonDoc = JsonDocument.Parse(responseJson);
            if (jsonDoc.RootElement.TryGetProperty("orderId", out JsonElement orderIdElement))
                return orderIdElement.GetInt64();

            throw new Exception($"Не удалось получить orderId: {responseJson}");
        }

        public async Task<string> GetOrderStatusAsync(long orderId, string accessToken)
        {
            string fullUrl = new Uri(new Uri(ApiEpguBaseUrl), $"order/{orderId}").ToString();
            using var request = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response = await _httpClient.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Ошибка проверки статуса: {response.StatusCode}. Ответ: {responseJson}");

            return responseJson;
        }

        public static (string Mnemonic, string ObjectType) ParseAttachmentLink(string link)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, "terrabyte", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Некорректная ссылка на файл результата: {link}");
            }

            string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new ArgumentException($"Некорректная ссылка на файл результата: {link}");

            string mnemonic = parts[^2];
            string objectType = parts[^1];
            if (string.IsNullOrWhiteSpace(mnemonic) || string.IsNullOrWhiteSpace(objectType))
                throw new ArgumentException($"Некорректная ссылка на файл результата: {link}");

            return (mnemonic, objectType);
        }

        public async Task DownloadResultFileAsync(long currentStatusHistoryId, string fileName, string token, string savePath)
        {
            // Очищаем токен от случайного префикса "Bearer ", если он есть
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(7).Trim();
            }

            string escapedFileName = Uri.EscapeDataString(fileName);
            string baseUrl = ApiEpguBaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/files/download/{currentStatusHistoryId}/3?mnemonic={escapedFileName}&eserviceCode=10000000374";

            // 1. Отключаем авто-редирект, чтобы управлять заголовками вручную
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var downloadClient = new HttpClient(handler);

            if (_httpClient.BaseAddress != null)
            {
                downloadClient.BaseAddress = _httpClient.BaseAddress;
            }

            // 2. Копируем все базовые заголовки (API-Key и прочие), КРОМЕ Authorization
            foreach (var header in _httpClient.DefaultRequestHeaders)
            {
                if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    continue;
                downloadClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }

            // ШАГ 1: Запрос к API ЕПГУ
            using var request1 = new HttpRequestMessage(HttpMethod.Get, url);
            request1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response1 = await downloadClient.SendAsync(request1, HttpCompletionOption.ResponseHeadersRead);
            HttpResponseMessage finalResponse = response1;

            // ШАГ 2: Ловим редирект (302) во внутреннее файловое хранилище (/api/storage)
            if ((int)response1.StatusCode >= 300 && (int)response1.StatusCode < 400 && response1.Headers.Location != null)
            {
                Uri redirectUri = response1.Headers.Location;
                if (!redirectUri.IsAbsoluteUri)
                {
                    Uri baseHost = _httpClient.BaseAddress ?? new Uri(baseUrl);
                    redirectUri = new Uri(baseHost, redirectUri);
                }

                using var request2 = new HttpRequestMessage(HttpMethod.Get, redirectUri);

                // КРИТИЧНО 1: ВОЗВРАЩАЕМ ТОКЕН! Внутреннее хранилище ЕПГУ требует авторизации.
                request2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // КРИТИЧНО 2: Передаем Cookies (балансировщик ЕПГУ часто выдает их в 302 ответе)
                if (response1.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    var cookiesToPass = string.Join("; ", setCookies.Select(c => c.Split(';')[0]));
                    request2.Headers.Add("Cookie", cookiesToPass);
                }

                finalResponse = await downloadClient.SendAsync(request2, HttpCompletionOption.ResponseHeadersRead);
            }

            // Проверяем итоговый результат
            if (!finalResponse.IsSuccessStatusCode)
            {
                string errorContent = await finalResponse.Content.ReadAsStringAsync();
                string step = finalResponse == response1 ? "Шаг 1 (API)" : "Шаг 2 (Хранилище)";
                throw new Exception($"{step}: Код {(int)finalResponse.StatusCode}. URL: {finalResponse.RequestMessage?.RequestUri}. Ответ: {errorContent}");
            }

            // Сохраняем файл
            using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await finalResponse.Content.CopyToAsync(fileStream);
        }
    }
}
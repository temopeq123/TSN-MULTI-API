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

        public async Task DownloadResultFileAsync(long currentStatusHistoryId, string mnemonic, string accessToken, string savePath)
        {
            // Обязательно кодируем имя файла (mnemonic), чтобы точки и спецсимволы не ломали URL
            string encodedMnemonic = Uri.EscapeDataString(mnemonic);

            string relativeUrl = $"files/download/{currentStatusHistoryId}/3?mnemonic={encodedMnemonic}&eserviceCode=10000000374";
            string fullUrl = new Uri(new Uri(ApiEpguBaseUrl), relativeUrl).ToString();

            using var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Ошибка скачивания файла ({response.StatusCode}): {errorContent}");
            }

            byte[] fileBytes = await response.Content.ReadAsByteArrayAsync();
            File.WriteAllBytes(savePath, fileBytes);
        }
    }
}
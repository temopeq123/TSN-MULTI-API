using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TSN_MULTI_API.Services
{
    public class ApiClient
    {
        private readonly HttpClient _httpClient;

        // Используем тестовый контур ЕПГУ, так как токен получаем из тестовой ЕСИА
        private const string ApiEpguBaseUrl = "https://svcdev-beta.test.gosuslugi.ru";

        public ApiClient()
        {
            _httpClient = new HttpClient();
        }

        public async Task<long> ReserveOrderIdAsync(string serviceCode, string targetCode, string regionCode, string token)
        {
            var requestBody = new
            {
                region = regionCode,
                serviceCode = serviceCode,
                targetCode = targetCode
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEpguBaseUrl}/api/gusmev/order");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"Код {(int)response.StatusCode}. Детали: {errorBody}");
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);
            return doc.RootElement.GetProperty("orderId").GetInt64();
        }

        public async Task SendChunkedArchiveAsync(long orderId, byte[] archiveData, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEpguBaseUrl}/api/gusmev/push/chunked");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(orderId.ToString()), "orderId");
            content.Add(new StringContent("1"), "chunks");
            content.Add(new StringContent("0"), "chunk");

            var fileContent = new ByteArrayContent(archiveData);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/zip");
            content.Add(fileContent, "file", "archive.zip");

            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"Код {(int)response.StatusCode}. Детали: {errorBody}");
            }
        }
                    public async Task<long> SendPushAsync(byte[] archiveData, string serviceCode, string targetCode, string region, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEpguBaseUrl}/api/gusmev/push");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var meta = new { region = region, serviceCode = serviceCode, targetCode = targetCode };

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(JsonSerializer.Serialize(meta), Encoding.UTF8, "application/json"), "meta");

            var fileContent = new ByteArrayContent(archiveData);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/zip");
            content.Add(fileContent, "file", "archive.zip");

            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"Код {(int)response.StatusCode}. Детали: {errorBody}");
            }
                 var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);
            return doc.RootElement.GetProperty("orderId").GetInt64();
        }

        public async Task<string> GetOrderStatusAsync(long orderId, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEpguBaseUrl}/api/gusmev/order/{orderId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"Код {(int)response.StatusCode}. Детали: {errorBody}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        public async Task DownloadResultFileAsync(long currentStatusHistoryId, string mnemonic, string token, string savePath)
        {
            string url = $"{ApiEpguBaseUrl}/api/gusmev/files/download/{currentStatusHistoryId}/3?mnemonic={mnemonic}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"Код {(int)response.StatusCode}. Детали: {errorBody}");
            }
            else
            {

                byte[] fileBytes = await response.Content.ReadAsByteArrayAsync();
                await System.IO.File.WriteAllBytesAsync(savePath, fileBytes);
            }
        }
        public static (string mnemonic, int objectType) ParseAttachmentLink(string link)
        {
            if (string.IsNullOrWhiteSpace(link)) return ("result", 3);
            var parts = link.Split('/');
            if (parts.Length >= 2)
            {
                string mnemonic = parts[parts.Length - 2];
                int.TryParse(parts[parts.Length - 1], out int objType);
                return (mnemonic, objType);
            }
            return ("result", 3);
        }
    }

    // ==============================================================================
    // БЛОК МОДЕЛЕЙ
    // ==============================================================================

    public class UserTemplateData
    {
        public string AuthorName { get; set; } = string.Empty;
        public string AuthorBorn { get; set; } = string.Empty;
        public string AuthorSnils { get; set; } = string.Empty;
        public string AuthorPhone { get; set; } = string.Empty;
        public string AuthorAddress { get; set; } = string.Empty;

        public string TargetIpNum { get; set; } = string.Empty;
        public string PetitionText { get; set; } = string.Empty;
    }

    public class OrganizationRecord
    {
        public string Name { get; set; } = string.Empty;
        public string Inn { get; set; } = string.Empty;
        public string Ogrn { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
    }
}
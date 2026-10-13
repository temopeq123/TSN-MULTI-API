using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TSN_MULTI_API.Core
{
    public enum FsspRequestType
    {
        CourseIP, // Ход ИП (10000000352)
        ExistIP   // Наличие ИП (60010153)
    }

    public class FsspService
    {
        private readonly ApiClient _apiClient;

        public FsspService(ApiClient apiClient)
        {
            _apiClient = apiClient;
        }

        public async Task<string> SendFSSpRequestAsync(FsspRequestType requestType, string deloNum, string snils, string fullName, string birthDate, string complainerType, string gender, string token, bool isLegalEntity = false, string compName = "", string compInn = "", string compOgrn = "", string compAddress = "", Action<string, string> onXmlGenerated = null)
        {
            string serviceCodeApi = requestType == FsspRequestType.CourseIP ? "10000000352" : "60010153";
            string targetCodeApi = requestType == FsspRequestType.CourseIP ? "-10000000352" : "-60010153";

            // 1. Резервирование номера заявления
            string orderId = await ReserveOrderIdAsync(serviceCodeApi, targetCodeApi, token);
            if (string.IsNullOrEmpty(orderId)) return "Ошибка: Сервер не вернул OrderId.";

            if (DateTime.TryParse(birthDate, out DateTime parsedDate))
                birthDate = parsedDate.ToString("yyyy-MM-dd");

            // 2. Формирование XML (Передаем isLegalEntity в GenerateReqXml для корректного ReceiverID)
            string reqXml = GenerateReqXml(orderId, requestType, isLegalEntity);
            string pievXml = GeneratePievEpguXml(orderId, requestType, deloNum, snils, fullName, birthDate, complainerType, gender, isLegalEntity, compName, compInn, compOgrn, compAddress);

            onXmlGenerated?.Invoke(reqXml, pievXml);

            // 3. Создание ZIP-архива
            byte[] zipData = CreateZipArchive(reqXml, pievXml);

            // 4. Эндпоинт для загрузки архивов всегда один
            string uploadEndpoint = "api/gusmev/push/chunked";

            // 5. Отправка заявления
            bool success = await SendChunkedArchiveAsync(orderId, zipData, token, uploadEndpoint);

            return success ? $"Заявка успешно отправлена. OrderId: {orderId}" : "Ошибка отправки архива.";
        }

        private string GenerateReqXml(string orderId, FsspRequestType requestType, bool isLegalEntity)
        {
            string dateNow = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            string statementDate = DateTime.Now.ToString("yyyy-MM-dd");

            string receiverId;
            if (requestType == FsspRequestType.ExistIP)
            {
                // Услуга "Наличие ИП" всегда отправляется на FSSP10
                receiverId = "FSSP10";
            }
            else
            {
                // Услуга "Ход ИП" отправляется на FSSP08 для ЮЛ и FSSP07 для ФЛ
                receiverId = isLegalEntity ? "FSSP08" : "FSSP07";
            }

            string targetCodeXml = requestType == FsspRequestType.CourseIP ? "10003818851" : "10001505301";

            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<fssp:EPGURequest xmlns:fssp=""urn://x-artifacts-fssp-ru/mvv/smev3/epgu/1.0.1"" Env=""SVCDEV"">
  <fssp:DataRequest>
    <fssp:OrderId>{orderId}</fssp:OrderId>
    <fssp:Date>{dateNow}</fssp:Date>
    <fssp:Department>ФССП</fssp:Department>
    <fssp:DepartmentCode>00000</fssp:DepartmentCode>
    <fssp:ReceiverID>{receiverId}</fssp:ReceiverID>
    <fssp:ServiceCode>10001449665</fssp:ServiceCode>
    <fssp:TargetCode>{targetCodeXml}</fssp:TargetCode>
    <fssp:StatementDate>{statementDate}</fssp:StatementDate>
  </fssp:DataRequest>
</fssp:EPGURequest>";
        }

        private string GeneratePievEpguXml(string orderId, FsspRequestType requestType, string deloNum, string snils, string fullName, string birthDate, string complainerType, string gender, bool isLegalEntity, string compName, string compInn, string compOgrn, string compAddress)
        {
            string docDate = DateTime.Now.ToString("yyyy-MM-dd");

            string docType = requestType == FsspRequestType.CourseIP ? "I_IPSIDE_FSSP_INTERACTIVE" : "I_IP_EXIST_INTERACTIVE";
            string docName = requestType == FsspRequestType.CourseIP
                ? "Заявление о предоставлении информации о ходе исполнительного производства из банка данных"
                : "Заявление о предоставлении информации о наличии исполнительного производства из банка данных";

            string includeAllTag = requestType == FsspRequestType.ExistIP ? "  <fssp:IncludeAll>true</fssp:IncludeAll>\n" : "";
            string deloNumTag = requestType == FsspRequestType.CourseIP ? $"  <fssp:DeloNum>{deloNum}</fssp:DeloNum>\n" : "";

            string trusteeBlock = "";
            if (isLegalEntity)
            {
                trusteeBlock = $@"  <fssp:TrusteeDoctype>07</fssp:TrusteeDoctype>
  <fssp:TrusteeDivision>{compName}</fssp:TrusteeDivision>
  <fssp:TrusteeDocnumber>-</fssp:TrusteeDocnumber>
  <fssp:TrusteeDocdate>{docDate}</fssp:TrusteeDocdate>
  <fssp:TrusteeName>{compName}</fssp:TrusteeName>
  <fssp:TrusteeAddress>{compAddress}</fssp:TrusteeAddress>
  <fssp:TrusteeInn>{compInn}</fssp:TrusteeInn>
  <fssp:TrusteeOGRN>{compOgrn}</fssp:TrusteeOGRN>
";
            }

            return $@"<fssp:IRequest xmlns:fssp=""http://www.fssprus.ru/namespace/incoming/2019/1"">
  <fssp:ExternalKey>{orderId}</fssp:ExternalKey>
  <fssp:DocType>{docType}</fssp:DocType>
  <fssp:DocName>{docName}</fssp:DocName>
  <fssp:DocDate>{docDate}</fssp:DocDate>
{includeAllTag}{deloNumTag}  <fssp:ComplainerType>{complainerType}</fssp:ComplainerType>
  <fssp:AuthorName>{fullName}</fssp:AuthorName>
  <fssp:ComplainerGender>{gender}</fssp:ComplainerGender>
  <fssp:AuthorBorn>{birthDate}</fssp:AuthorBorn>
  <fssp:AuthorSnils>{snils}</fssp:AuthorSnils>
  <fssp:AuthorBackAddrType>ЕПГУ</fssp:AuthorBackAddrType>
  <fssp:AuthorBackAddr>{snils}</fssp:AuthorBackAddr>
{trusteeBlock}  <fssp:SimpleDigSignature>{snils}</fssp:SimpleDigSignature>
  <fssp:Sendlist>
    <fssp:Receiver>ФССП</fssp:Receiver>
    <fssp:ReceiverDivisionCode>00000</fssp:ReceiverDivisionCode>
    <fssp:ReceiverAddrType>ВЕБ-СЕРВИС</fssp:ReceiverAddrType>
    <fssp:ReceiverAddr>00000</fssp:ReceiverAddr>
  </fssp:Sendlist>
</fssp:IRequest>";
        }

        private async Task<string> ReserveOrderIdAsync(string serviceCode, string targetCode, string token)
        {
            using var client = new HttpClient();
            client.BaseAddress = new Uri("https://svcdev-beta.test.gosuslugi.ru/");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var requestBody = new { region = "45000000000", serviceCode = serviceCode, targetCode = targetCode };
            string jsonBody = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("api/gusmev/order", content);
            response.EnsureSuccessStatusCode();

            string responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("orderId", out var orderIdProp))
                return orderIdProp.GetInt64().ToString();
            if (doc.RootElement.TryGetProperty("id", out var idProp))
                return idProp.GetInt64().ToString();

            throw new Exception("Не удалось распарсить OrderId из ответа ЕПГУ: " + responseJson);
        }

        private async Task<bool> SendChunkedArchiveAsync(string orderId, byte[] zipData, string token, string endpoint)
        {
            using var client = new HttpClient();
            client.BaseAddress = new Uri("https://svcdev-beta.test.gosuslugi.ru/");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(orderId), "orderId");
            content.Add(new StringContent("1"), "chunks");
            content.Add(new StringContent("0"), "chunk");

            var fileContent = new ByteArrayContent(zipData);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            content.Add(fileContent, "file", "payload.zip");

            var response = await client.PostAsync(endpoint, content);
            response.EnsureSuccessStatusCode();

            return true;
        }

        private byte[] CreateZipArchive(string reqXml, string pievXml)
        {
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    var reqEntry = archive.CreateEntry("req.xml");
                    using (var entryStream = reqEntry.Open())
                    using (var sw = new StreamWriter(entryStream, Encoding.UTF8))
                        sw.Write(reqXml);

                    var pievEntry = archive.CreateEntry("piev_epgu.xml");
                    using (var entryStream = pievEntry.Open())
                    using (var sw = new StreamWriter(entryStream, Encoding.UTF8))
                        sw.Write(pievXml);
                }
                return ms.ToArray();
            }
        }
    }
}
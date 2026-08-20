using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

// Подключаем все нужные пространства имен:
using TSN_MULTI_API.Core;
using TSN_MULTI_API.Services;

// ЖЕСТКО разрешаем конфликты дубликатов (берем оригинальные классы из Core):
using OrderRecord = TSN_MULTI_API.Core.OrderRecord;
using ResultFileInfo = TSN_MULTI_API.Core.ResultFileInfo;

namespace TSN_MULTI_API
{
    public partial class MainWindow : Window
    {
        private readonly AuthManager _authManager;
        private readonly ApiClient _apiClient;
        private readonly ArchiveBuilder _archiveBuilder;
        private List<OrderRecord> _ordersList = new List<OrderRecord>();
        private bool _isFormattingSnils = false;

        public MainWindow()
        {
            InitializeComponent();
            _authManager = new AuthManager();
            _apiClient = new ApiClient();
            _archiveBuilder = new ArchiveBuilder();

            LoadCertificates();
            LoadHistoryToUI();
            LoadOrganizations();

            TxtHeader.Text = "Документы (PDF)";
        }

        private void LoadOrganizations()
        {
            CmbOrganizations.Items.Add(new TSN_MULTI_API.Services.OrganizationRecord
            {
                Name = "ТСН \"Сапожок\"",
                Inn = "5011013466",
                Ogrn = "1105011000555",
                Address = "140304, Московская обл, г Егорьевск, тер. ТСН Сапожок, стр. 51"
            });
            CmbOrganizations.SelectedIndex = 0;
        }

        private void Log(string message)
        {
            TxtStatus.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            TxtStatus.ScrollToEnd();
        }

        private void LoadHistoryToUI()
        {
            _ordersList = HistoryManager.LoadHistory();
            OrdersListView.ItemsSource = null;
            OrdersListView.ItemsSource = _ordersList;
        }

        private void LoadCertificates()
        {
            try
            {
                var byThumbprint = new Dictionary<string, CertificateItem>(StringComparer.OrdinalIgnoreCase);
                foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                {
                    using X509Store store = new X509Store(StoreName.My, location);
                    store.Open(OpenFlags.ReadOnly);
                    foreach (X509Certificate2 certificate in store.Certificates.Cast<X509Certificate2>())
                    {
                        if (!certificate.HasPrivateKey || string.IsNullOrWhiteSpace(certificate.Thumbprint))
                            continue;

                        string thumbprint = CryptoSigner.NormalizeThumbprint(certificate.Thumbprint);
                        if (byThumbprint.ContainsKey(thumbprint))
                            continue;

                        byThumbprint[thumbprint] = new CertificateItem(
                            ParseCommonName(certificate.Subject),
                            thumbprint,
                            location.ToString(),
                            certificate.NotAfter);
                    }
                }

                var validCerts = byThumbprint.Values
                    .OrderByDescending(c => c.NotAfter)
                    .ToList();

                CertComboBox.ItemsSource = validCerts;
                if (validCerts.Count > 0)
                    CertComboBox.SelectedIndex = 0;

                Log($"Найдено сертификатов: {validCerts.Count}");
            }
            catch (Exception ex)
            {
                Log($"Ошибка загрузки сертификатов: {FormatException(ex)}");
            }
        }

        private void RefreshCerts_Click(object sender, RoutedEventArgs e) => LoadCertificates();

        private void BtnCertProperties_Click(object sender, RoutedEventArgs e)
        {
            if (CertComboBox.SelectedItem is not CertificateItem selected)
            {
                MessageBox.Show("Выберите сертификат.");
                return;
            }
            try
            {
                using var certificate = CryptoSigner.FindCertificate(selected.Thumbprint);
                MessageBox.Show(
                    $"Субъект: {selected.SubjectName}\n" +
                    $"Отпечаток: {selected.Thumbprint}\n" +
                    $"Хранилище: {selected.StoreLocation}\n" +
                    $"Действителен до: {certificate.NotAfter:dd.MM.yyyy HH:mm:ss}\n" +
                    $"Ключ CryptoPro: {(certificate.HasPrivateKey ? "Присутствует" : "Отсутствует")}\n" +
                    $"Алгоритм: {certificate.PublicKey.Oid.Value}",
                    "Свойства сертификата",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(FormatException(ex), "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private sealed record CertificateItem(
            string SubjectName,
            string Thumbprint,
            string StoreLocation,
            DateTime NotAfter);

        private static string FormatException(Exception ex)
        {
            var parts = new List<string>();
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message))
                    parts.Add(current.Message);
            }
            return string.Join("\nВызвано: ", parts.Distinct());
        }

        private string ParseCommonName(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return "";
            var match = System.Text.RegularExpressions.Regex.Match(subject, @"CN=([^,]+)");
            return match.Success ? match.Groups[1].Value : subject;
        }

        private void BtnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = "PDF документы (*.pdf)|*.pdf|Все файлы (*.*)|*.*" };
            if (dlg.ShowDialog() == true) TxtFilePath.Text = dlg.FileName;
        }

        private void TxtSnils_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isFormattingSnils) return;
            if (sender is not TextBox txtBox) return;

            string text = new string(txtBox.Text.Where(char.IsDigit).ToArray());
            if (text.Length > 11) text = text.Substring(0, 11);

            string formatted = "";
            for (int i = 0; i < text.Length; i++)
            {
                if (i == 3 || i == 6) formatted += "-";
                else if (i == 9) formatted += " ";
                formatted += text[i];
            }

            _isFormattingSnils = true;
            txtBox.Text = formatted;
            txtBox.SelectionStart = formatted.Length;
            _isFormattingSnils = false;
        }

        // ==========================================
        // БЛОК ГОСКЛЮЧА
        // ==========================================

        private async void PushArchiveSmartAsync_Click(object sender, RoutedEventArgs e)
        {
            if (CertComboBox.SelectedValue == null || string.IsNullOrWhiteSpace(TxtFilePath.Text))
            {
                MessageBox.Show("Заполните все поля!");
                return;
            }
            string thumbprint = CertComboBox.SelectedValue.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = TxtManualTokenBox.Text.Trim();
            try
            {
                Log("--- Отправка в Госключ ---");
                if (string.IsNullOrEmpty(token))
                {
                    Log("Получаем AccessToken...");
                    token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                    Log("AccessToken получен.");
                }
                else
                {
                    Log("Используется введенный AccessToken.");
                }
                Log("Создание XML и CMS detached-подписи...");
                byte[] archiveBytes = _archiveBuilder.BuildArchive(TxtFilePath.Text, TxtSnils.Text, TxtDescription.Text, thumbprint);
                Log("Архив собран.");

                long orderId = await _apiClient.SendPushAsync(archiveBytes, "10000000214", "10000000214", "45000000000", token);

                var record = new OrderRecord
                {
                    OrderId = orderId,
                    FileName = Path.GetFileName(TxtFilePath.Text),
                    Snils = TxtSnils.Text,
                    Description = TxtDescription.Text,
                    CreatedAt = DateTime.Now,
                    StatusName = "Отправлено"
                };
                HistoryManager.AddOrUpdate(record);
                LoadHistoryToUI();
                Log($"Успешно. Order ID: {orderId}");
                Log("Проверка статуса...");
                await RefreshOrderStatusAsync(record, token);
            }
            catch (Exception ex)
            {
                Log($"Ошибка: {FormatException(ex)}");
                MessageBox.Show(FormatException(ex), "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnCheckSelectedStatus_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord selectedOrder)
            {
                MessageBox.Show("Выберите запись из истории!");
                return;
            }
            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = TxtManualTokenBox.Text.Trim();
            try
            {
                Log($"Проверка статуса Order ID: {selectedOrder.OrderId}...");
                if (string.IsNullOrEmpty(token)) token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                await RefreshOrderStatusAsync(selectedOrder, token);
            }
            catch (Exception ex)
            {
                Log($"Ошибка проверки: {ex.Message}");
            }
        }

        private async void BtnDownloadSignedFiles_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord selectedOrder)
            {
                MessageBox.Show("Выберите запись из истории!");
                return;
            }
            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = TxtManualTokenBox.Text.Trim();
            try
            {
                Log($"Скачивание результатов Order ID: {selectedOrder.OrderId}...");
                if (string.IsNullOrEmpty(token))
                    token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                await RefreshOrderStatusAsync(selectedOrder, token);
                if (!IsDocumentsSigned(selectedOrder.StatusName))
                {
                    MessageBox.Show(
                        "Документы еще не подписаны или произошел отказ: " + selectedOrder.StatusName,
                        "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"Подписано_{selectedOrder.OrderId}");
                Directory.CreateDirectory(saveDir);
                var originalFiles = new List<string> { selectedOrder.FileName };
                foreach (string baseFileName in originalFiles)
                {
                    if (string.IsNullOrWhiteSpace(baseFileName))
                        continue;
                    string savePath = Path.Combine(saveDir, baseFileName + ".sig");
                    try
                    {
                        await _apiClient.DownloadResultFileAsync(
                            selectedOrder.CurrentStatusHistoryId,
                            baseFileName,
                            token,
                            savePath);
                        Log($"Сохранен файл: {baseFileName}.sig");
                    }
                    catch (Exception ex)
                    {
                        Log($"Ошибка скачивания {baseFileName}: {ex.Message}");
                    }
                }
                Log($"Файлы сохранены в папку: {saveDir}");
                MessageBox.Show($"Файлы успешно сохранены:\n{saveDir}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"Ошибка скачивания: {ex.Message}");
                MessageBox.Show($"Ошибка скачивания: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RefreshOrderStatusAsync(OrderRecord order, string token)
        {
            string responseJson = await _apiClient.GetOrderStatusAsync(order.OrderId, token);
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("order", out var orderProp))
            {
                if (orderProp.ValueKind == JsonValueKind.String)
                {
                    string? orderStr = orderProp.GetString();
                    if (!string.IsNullOrWhiteSpace(orderStr))
                    {
                        using var orderDoc = JsonDocument.Parse(orderStr);
                        ApplyOrderDetails(order, orderDoc.RootElement);
                    }
                }
                else if (orderProp.ValueKind == JsonValueKind.Object)
                {
                    ApplyOrderDetails(order, orderProp);
                }
            }
            order.IsCompleted = order.StatusName.Contains("подписан", StringComparison.OrdinalIgnoreCase) ||
                                IsDeliveryFailure(order.StatusName);
            HistoryManager.AddOrUpdate(order);
            LoadHistoryToUI();
            if (IsDeliveryFailure(order.StatusName))
            {
                Log("Статус: " + order.StatusName + ". Завершено с ошибкой.");
            }
            else
            {
                Log(
                    "Статус: " + order.StatusName + FormatStateCode(order.StateOrgStatusCode) +
                    $"; ID истории: {order.CurrentStatusHistoryId}; Файлов: {(order.HasResult ? order.ResultFiles.Count : 0)}.");
            }
        }

        private static void ApplyOrderDetails(OrderRecord order, JsonElement orderElement)
        {
            if (orderElement.TryGetProperty("orderStatusName", out var statusNameProp))
                order.StatusName = statusNameProp.GetString() ?? "";
            if (orderElement.TryGetProperty("stateOrgStatusCode", out var stateCodeProp))
                order.StateOrgStatusCode = stateCodeProp.GetString() ?? string.Empty;
            if (orderElement.TryGetProperty("currentStatusHistoryId", out var histIdProp) &&
                histIdProp.ValueKind == JsonValueKind.Number)
                order.CurrentStatusHistoryId = histIdProp.GetInt64();
            if (orderElement.TryGetProperty("hasResult", out var hasResultProp) &&
                hasResultProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                order.HasResult = hasResultProp.GetBoolean();
            if (orderElement.TryGetProperty("orderResponseFiles", out var responseFilesProp) &&
                responseFilesProp.ValueKind == JsonValueKind.Array)
            {
                // ИСПРАВЛЕНИЕ ОШИБКИ "!=" - теперь используем "is not null"
                order.ResultFiles = responseFilesProp
                    .EnumerateArray()
                    .Select(ParseResultFile)
                    .Where(file => file is not null)
                    .Select(file => file!)
                    .GroupBy(file => file.Link, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            }
        }

        private static ResultFileInfo? ParseResultFile(JsonElement file)
        {
            if (!file.TryGetProperty("link", out var linkProp))
                return null;
            string? link = linkProp.GetString();
            if (string.IsNullOrWhiteSpace(link))
                return null;
            string fileName = string.Empty;
            if (file.TryGetProperty("fileName", out var fileNameProp))
                fileName = fileNameProp.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                try
                {
                    var parsedLinkData = ApiClient.ParseAttachmentLink(link);
                    fileName = parsedLinkData.mnemonic;
                }
                catch
                {
                    fileName = "result";
                }
            }
            return new ResultFileInfo
            {
                FileName = fileName,
                Link = link
            };
        }

        private static string FormatStateCode(string? stateCode) =>
            string.IsNullOrWhiteSpace(stateCode) ? string.Empty : $" [{stateCode}]";

        private static bool IsDeliveryFailure(string? statusName) =>
            !string.IsNullOrWhiteSpace(statusName) &&
            (statusName.Contains("отказ", StringComparison.OrdinalIgnoreCase) ||
             statusName.Contains("ошибк", StringComparison.OrdinalIgnoreCase) ||
             statusName.Contains("отменен", StringComparison.OrdinalIgnoreCase) ||
             statusName.Contains("возврат", StringComparison.OrdinalIgnoreCase));

        private static bool IsDocumentsSigned(string? statusName) =>
    !string.IsNullOrWhiteSpace(statusName) &&
    (statusName.Contains("подписан", StringComparison.OrdinalIgnoreCase) ||
     statusName.Contains("Услуга оказана", StringComparison.OrdinalIgnoreCase)) && // <-- Добавили проверку статуса ФССП
    !IsDeliveryFailure(statusName);

        private static bool IsDocumentsSigned(string? statusName, string? stateOrgStatusCode) =>
            (!string.IsNullOrWhiteSpace(stateOrgStatusCode) && stateOrgStatusCode.Contains("SIGNED", StringComparison.OrdinalIgnoreCase))
            || IsDocumentsSigned(statusName);

        // ==========================================
        // БЛОК ФССП 
        // ==========================================

        private async void BtnSendFssp_Click(object sender, RoutedEventArgs e)
        {
            if (CmbFsspService.SelectedItem is not ComboBoxItem selectedService || selectedService.Tag == null)
            {
                MessageBox.Show("Укажите тип услуги!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CmbOrganizations.SelectedItem is not TSN_MULTI_API.Services.OrganizationRecord selectedOrg)
            {
                MessageBox.Show("Выберите организацию из списка!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var txtAuthorBorn = (TextBox?)this.FindName("TxtAuthorBorn");
            var txtAuthorPhone = (TextBox?)this.FindName("TxtAuthorPhone");
            var txtIpNum = (TextBox?)this.FindName("TxtIpNum");
            var txtPetitionText = (TextBox?)this.FindName("TxtPetitionText");

            string authorName = TxtAuthorName.Text.Trim();
            string authorBorn = txtAuthorBorn?.Text.Trim() ?? "2004-07-29";
            string authorPhone = txtAuthorPhone?.Text.Trim() ?? "+7(000)0000000";
            string targetIpNum = txtIpNum?.Text.Trim() ?? "";
            string petitionText = txtPetitionText?.Text.Trim() ?? "";

            string cleanSnils = new string(TxtFsspSnils.Text.Where(char.IsDigit).ToArray());

            if (string.IsNullOrWhiteSpace(authorName))
            {
                MessageBox.Show("Укажите ФИО заявителя!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (cleanSnils.Length != 11)
            {
                MessageBox.Show("Введите корректный СНИЛС (11 цифр)!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string eServiceCode = selectedService.Tag.ToString() ?? "";

            if ((eServiceCode == "10000000352" || eServiceCode == "10000000367") && string.IsNullOrWhiteSpace(targetIpNum))
            {
                MessageBox.Show("Для этой услуги необходимо указать номер Исполнительного производства!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                BtnSendFssp.IsEnabled = false;
                string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
                string apiKey = TxtApiKeyBox.Text.Trim();
                string token = TxtManualTokenBox.Text.Trim();

                if (string.IsNullOrEmpty(token))
                {
                    Log("Получаем AccessToken...");
                    token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                }

                Log($"--- Запуск услуги ФССП (Код {eServiceCode}) ---");
                Log($"Организация: {selectedOrg.Name}, ИНН: {selectedOrg.Inn}");

                var config = FsspConfigProvider.GetConfig(eServiceCode, isLegalEntity: true);

                // Для резервирования номера используем портальные коды (EServiceCode), а не СМЭВ!
                string portalTargetCode = "-" + config.EServiceCode;
                long orderId = await _apiClient.ReserveOrderIdAsync(config.EServiceCode, portalTargetCode, "45000000000", token);
                string orderIdStr = orderId.ToString();
                Log($"OrderId успешно зарезервирован: {orderIdStr}");

                var xmlBuilder = new FsspXmlBuilder();
                string reqXml = xmlBuilder.BuildReqXml(orderIdStr, config);
                string pievXml = string.Empty;

                switch (eServiceCode)
                {
                    case "60010153":
                        pievXml = xmlBuilder.BuildPievEpguExistInteractive(
                            orderIdStr, authorName, cleanSnils, selectedOrg.Name, selectedOrg.Inn, selectedOrg.Ogrn, selectedOrg.Address, authorBorn);
                        break;

                    case "10000000352":
                        pievXml = xmlBuilder.BuildPievEpguCourseIp(
                            orderIdStr, targetIpNum, authorName, cleanSnils, selectedOrg.Name, selectedOrg.Inn, selectedOrg.Ogrn, selectedOrg.Address, authorBorn);
                        break;

                    case "10000000367":
                        string docType = "I_REQ_SPI_PETITION";
                        string docName = "Заявление (ходатайство) стороны исполнительного производства";
                        pievXml = xmlBuilder.BuildPievEpguPetition(
                            orderIdStr, docType, docName, petitionText, targetIpNum, authorName, cleanSnils, authorBorn, authorPhone, selectedOrg.Address);
                        break;
                }

                var fsspZipBuilder = new FsspZipBuilder();
                byte[] zipFile = fsspZipBuilder.CreateZip(reqXml, pievXml);

                await _apiClient.SendChunkedArchiveAsync(orderId, zipFile, token);

                var record = new OrderRecord
                {
                    OrderId = orderId,
                    FileName = $"Запрос ФССП ({selectedOrg.Inn})",
                    Snils = cleanSnils,
                    Description = $"Тип: {eServiceCode} ИП: {targetIpNum}",
                    CreatedAt = DateTime.Now,
                    StatusName = "Отправлено через API"
                };

                HistoryManager.AddOrUpdate(record);
                LoadHistoryToUI();

                Log("Запрос успешно отправлен.");
                MessageBox.Show($"Запрос для {selectedOrg.Name} отправлен.\nНомер: {orderId}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"Ошибка ФССП API: {ex.Message}");
                MessageBox.Show($"Сбой при отправке API ФССП:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSendFssp.IsEnabled = true;
            }
        }

        private void MenuGoskluch_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
            TxtHeader.Text = "Документы (PDF)";
        }

        private void MenuFssp_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 1;
            TxtHeader.Text = "Услуги ФССП (API СМЭВ 3)";
        }
    }
}
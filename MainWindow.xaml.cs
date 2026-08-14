using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using Microsoft.Win32;
using TSN_MULTI_API.Core;

namespace TSN_MULTI_API
{
    public partial class MainWindow : Window
    {
        private readonly AuthManager _authManager;
        private readonly ApiClient _apiClient;
        private readonly ArchiveBuilder _archiveBuilder;
        private List<OrderRecord> _ordersList = new List<OrderRecord>();

        public MainWindow()
        {
            InitializeComponent();
            _authManager = new AuthManager();
            _apiClient = new ApiClient();
            _archiveBuilder = new ArchiveBuilder();

            LoadCertificates();
            LoadHistoryToUI();
            TxtHeader.Text = "Госключ - Управление и История";
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

                Log($"Сертификатов с закрытым ключом: {validCerts.Count}");
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
                MessageBox.Show("Сначала выберите сертификат.");
                return;
            }

            try
            {
                using var certificate = CryptoSigner.FindCertificate(selected.Thumbprint);
                MessageBox.Show(
                    $"Сертификат: {selected.SubjectName}\n" +
                    $"Отпечаток: {selected.Thumbprint}\n" +
                    $"Хранилище: {selected.StoreLocation}\n" +
                    $"Срок действия до: {certificate.NotAfter:dd.MM.yyyy HH:mm:ss}\n" +
                    $"Закрытый ключ CryptoPro: {(certificate.HasPrivateKey ? "ДОСТУПЕН" : "НЕДОСТУПЕН")}\n" +
                    $"Алгоритм: {certificate.PublicKey.Oid.Value}",
                    "Свойства сертификата",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(FormatException(ex), "Сертификат не готов к подписи", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            return string.Join("\nПричина: ", parts.Distinct());
        }

        private string ParseCommonName(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return "Неизвестно";
            var match = System.Text.RegularExpressions.Regex.Match(subject, @"CN=([^,]+)");
            return match.Success ? match.Groups[1].Value : subject;
        }

        private void BtnSelectFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = "PDF файлы (*.pdf)|*.pdf|Все файлы (*.*)|*.*" };
            if (dlg.ShowDialog() == true) TxtFilePath.Text = dlg.FileName;
        }

        private bool _isFormattingSnils = false;
        private void TxtSnils_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isFormattingSnils) return;
            string text = new string(TxtSnils.Text.Where(char.IsDigit).ToArray());
            if (text.Length > 11) text = text.Substring(0, 11);
            string formatted = "";
            for (int i = 0; i < text.Length; i++)
            {
                if (i == 3 || i == 6) formatted += "-";
                else if (i == 9) formatted += " ";
                formatted += text[i];
            }
            _isFormattingSnils = true;
            TxtSnils.Text = formatted;
            TxtSnils.SelectionStart = formatted.Length;
            _isFormattingSnils = false;
        }

        private async void PushArchiveSmartAsync_Click(object sender, RoutedEventArgs e)
        {
            if (CertComboBox.SelectedValue == null || string.IsNullOrWhiteSpace(TxtFilePath.Text))
            {
                MessageBox.Show("Выберите сертификат и файл!");
                return;
            }

            string thumbprint = CertComboBox.SelectedValue.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = TxtManualTokenBox.Text.Trim();

            try
            {
                Log("--- Отправка документа в Госключ ---");
                if (string.IsNullOrEmpty(token))
                {
                    Log("Создание подписи API-Key и получение accessTkn...");
                    token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                    Log("AccessTkn успешно получен.");
                }
                else
                {
                    Log("Используется вручную введённый AccessTkn.");
                }

                Log("Формирование XML и двух CMS detached-подписей для архива...");
                byte[] archiveBytes = _archiveBuilder.BuildArchive(TxtFilePath.Text, TxtSnils.Text, TxtDescription.Text, thumbprint);
                Log("CMS-подписи успешно сформированы и локально проверены.");
                long orderId = await _apiClient.SendPushAsync(archiveBytes, token);

                var record = new OrderRecord
                {
                    OrderId = orderId,
                    FileName = Path.GetFileName(TxtFilePath.Text),
                    Snils = TxtSnils.Text,
                    Description = TxtDescription.Text,
                    CreatedAt = DateTime.Now,
                    StatusName = "Принята порталом, ожидается обработка"
                };
                HistoryManager.AddOrUpdate(record);
                LoadHistoryToUI();

                Log($"Заявка принята порталом. Order ID: {orderId}. Это ещё не подтверждение доставки в Госключ.");
                Log("Проверка фактической доставки в Госключ...");
                await RefreshOrderStatusAsync(record, token);
            }
            catch (Exception ex)
            {
                Log($"ОШИБКА: {FormatException(ex)}");
                MessageBox.Show(
                    FormatException(ex),
                    "Ошибка отправки",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnCheckSelectedStatus_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord selectedOrder)
            {
                MessageBox.Show("Выберите заявление из таблицы истории!");
                return;
            }

            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = TxtManualTokenBox.Text.Trim();

            try
            {
                Log($"Проверка статуса для Order ID: {selectedOrder.OrderId}...");
                if (string.IsNullOrEmpty(token)) token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                await RefreshOrderStatusAsync(selectedOrder, token);
            }
            catch (Exception ex)
            {
                Log($"Ошибка проверки статуса: {ex.Message}");
            }
        }

        private async void BtnDownloadSignedFiles_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord selectedOrder)
            {
                MessageBox.Show("Выберите заявку в списке!");
                return;
            }

            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = TxtManualTokenBox.Text.Trim();

            try
            {
                Log($"Скачивание подписанных файлов для Order ID: {selectedOrder.OrderId}...");

                if (string.IsNullOrEmpty(token))
                    token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);

                await RefreshOrderStatusAsync(selectedOrder, token);

                if (!IsDocumentsSigned(selectedOrder.StatusName))
                {
                    MessageBox.Show(
                        "Документы еще не подписаны.\nТекущий статус: " + selectedOrder.StatusName,
                        "Внимание",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"Госключ_{selectedOrder.OrderId}");
                Directory.CreateDirectory(saveDir);

                // Исходные имена файлов СТРОГО как они отправлялись
                var originalFiles = new List<string> { selectedOrder.FileName, "req.xml" };

                foreach (string baseFileName in originalFiles)
                {
                    if (string.IsNullOrWhiteSpace(baseFileName))
                        continue;

                    // На диск сохраняем с расширением .sig
                    string savePath = Path.Combine(saveDir, baseFileName + ".sig");

                    try
                    {
                        // СТРОГО ПО СПЕЦИФИКАЦИИ: 
                        // 1. Используем CurrentStatusHistoryId
                        // 2. Передаем оригинальное имя файла (baseFileName) без .sig
                        await _apiClient.DownloadResultFileAsync(
                            selectedOrder.CurrentStatusHistoryId,
                            baseFileName,
                            token,
                            savePath);

                        Log($"Успешно скачан файл подписи: {baseFileName}.sig");
                    }
                    catch (Exception ex)
                    {
                        Log($"Не удалось скачать подпись для {baseFileName}: {ex.Message}");
                    }
                }

                Log($"Скачивание завершено. Папка: {saveDir}");
                MessageBox.Show($"Файлы подписей сохранены в папку:\n{saveDir}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"Ошибка: {ex.Message}");
                MessageBox.Show($"Произошла ошибка при скачивании.\nДетали: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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
                Log("ДОКУМЕНТ НЕ ДОСТАВЛЕН В ГОСКЛЮЧ: " + order.StatusName +
                    ". Проверьте СНИЛС и наличие учётной записи тестера в тестовой ЕСИА.");
            }
            else
            {
                Log(
                    "Статус заявки: " + order.StatusName + FormatStateCode(order.StateOrgStatusCode) +
                    $"; ID истории: {order.CurrentStatusHistoryId}; результат: {(order.HasResult ? order.ResultFiles.Count : 0)} файл(ов).");
            }
        }

        private static void ApplyOrderDetails(OrderRecord order, JsonElement orderElement)
        {
            if (orderElement.TryGetProperty("orderStatusName", out var statusNameProp))
                order.StatusName = statusNameProp.GetString() ?? "Неизвестно";

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
                order.ResultFiles = responseFilesProp
                    .EnumerateArray()
                    .Select(ParseResultFile)
                    .Where(file => file is not null)
                    .Cast<ResultFileInfo>()
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
                    (string mnemonic, _) = ApiClient.ParseAttachmentLink(link);
                    fileName = mnemonic;
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
            (statusName.Contains("не найдена", StringComparison.OrdinalIgnoreCase) ||
             statusName.Contains("отклон", StringComparison.OrdinalIgnoreCase) ||
             statusName.Contains("ошиб", StringComparison.OrdinalIgnoreCase) ||
             statusName.Contains("истек", StringComparison.OrdinalIgnoreCase));

        private static bool IsDocumentsSigned(string? statusName) =>
            !string.IsNullOrWhiteSpace(statusName) &&
            statusName.Contains("подписан", StringComparison.OrdinalIgnoreCase) &&
            !IsDeliveryFailure(statusName);

        // Overload accepting stateOrgStatusCode for cases when API returns status code instead of human-friendly text
        private static bool IsDocumentsSigned(string? statusName, string? stateOrgStatusCode) =>
            (!string.IsNullOrWhiteSpace(stateOrgStatusCode) && stateOrgStatusCode.Contains("SIGNED", StringComparison.OrdinalIgnoreCase))
            || IsDocumentsSigned(statusName);
    }
}

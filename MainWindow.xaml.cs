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
                MessageBox.Show("Выберите заявление из таблицы!");
                return;
            }

            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = TxtManualTokenBox.Text.Trim();

            try
            {
                Log($"Скачивание подписанных файлов для Order ID: {selectedOrder.OrderId}...");
                if (string.IsNullOrEmpty(token)) token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                await RefreshOrderStatusAsync(selectedOrder, token);

                if (!IsDocumentsSigned(selectedOrder.StatusName) || !selectedOrder.HasResult)
                {
                    MessageBox.Show(
                        "Файлы результата доступны только после статуса «Документы подписаны» и появления результата.\n" +
                        "Текущий статус: " + selectedOrder.StatusName + FormatStateCode(selectedOrder.StateOrgStatusCode),
                        "Результат ещё недоступен",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (selectedOrder.CurrentStatusHistoryId == 0 || selectedOrder.ResultFileNames.Count == 0)
                    throw new Exception("ЕПГУ сообщил о подписании, но не вернул идентификатор статуса или имена файлов результата.");

                string saveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"Госключ_{selectedOrder.OrderId}");
                Directory.CreateDirectory(saveDir);

                foreach (string resultFileName in selectedOrder.ResultFileNames)
                {
                    string safeFileName = Path.GetFileName(resultFileName);
                    if (string.IsNullOrWhiteSpace(safeFileName))
                        continue;

                    await _apiClient.DownloadResultFileAsync(
                        selectedOrder.CurrentStatusHistoryId,
                        safeFileName,
                        token,
                        Path.Combine(saveDir, safeFileName));
                }

                Log($"Файлы успешно скачаны в папку: {saveDir}");
                MessageBox.Show($"Файлы успешно скачаны!\nПуть: {saveDir}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"Ошибка скачивания: {ex.Message}");
                MessageBox.Show($"Не удалось скачать файлы. Убедитесь, что статус 'Документы подписаны'.\nОшибка: {ex.Message}", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task RefreshOrderStatusAsync(OrderRecord order, string token)
        {
            string responseJson = await _apiClient.GetOrderStatusAsync(order.OrderId, token);
            using var doc = JsonDocument.Parse(responseJson);

            if (doc.RootElement.TryGetProperty("order", out var orderProp))
            {
                string? orderStr = orderProp.GetString();
                if (!string.IsNullOrWhiteSpace(orderStr))
                {
                    using var orderDoc = JsonDocument.Parse(orderStr);
                    if (orderDoc.RootElement.TryGetProperty("orderStatusName", out var statusNameProp))
                        order.StatusName = statusNameProp.GetString() ?? "Неизвестно";

                    if (orderDoc.RootElement.TryGetProperty("stateOrgStatusCode", out var stateCodeProp))
                        order.StateOrgStatusCode = stateCodeProp.GetString() ?? string.Empty;

                    if (orderDoc.RootElement.TryGetProperty("currentStatusHistoryId", out var histIdProp) &&
                        histIdProp.ValueKind == JsonValueKind.Number)
                        order.CurrentStatusHistoryId = histIdProp.GetInt64();

                    if (orderDoc.TryGetProperty("hasResult", out var hasResultProp) &&
                        (hasResultProp.ValueKind is JsonValueKind.True or JsonValueKind.False))
                        order.HasResult = hasResultProp.GetBoolean();

                    if (orderDoc.TryGetProperty("orderResponseFiles", out var responseFilesProp) &&
                        responseFilesProp.ValueKind == JsonValueKind.Array)
                    {
                        order.ResultFileNames = responseFilesProp
                            .EnumerateArray()
                            .Where(file => file.TryGetProperty("fileName", out var fileNameProp) &&
                                           !string.IsNullOrWhiteSpace(fileNameProp.GetString()))
                            .Select(file => file.GetProperty("fileName").GetString()!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
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
                    $"; ID истории: {order.CurrentStatusHistoryId}; результат: {(order.HasResult ? order.ResultFileNames.Count : 0)} файл(ов).");
            }
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
            string.Equals(statusName?.Trim(), "Документы подписаны", StringComparison.OrdinalIgnoreCase);
    }
}

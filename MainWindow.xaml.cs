using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
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
        private List<string> _selectedFiles = new List<string>();
        private List<AddressBookEntry> _addressBookEntries = new List<AddressBookEntry>();
        private Guid? _editingAddressBookId;

        private SignatureType _selectedSignatureType = SignatureType.Ukep;
        private long? _selectedOrderId;

        private readonly SettingsManager _settingsManager;
        private readonly System.Windows.Threading.DispatcherTimer _pollingTimer;

        public MainWindow()
        {
            InitializeComponent();
            _authManager = new AuthManager();
            _apiClient = new ApiClient();
            _archiveBuilder = new ArchiveBuilder();

            _settingsManager = new SettingsManager();
            ApplySettingsToUi();

            LoadCertificates();
            LoadAddressBook();
            LoadHistoryToUI();
            OrdersListView.SelectionChanged += OrdersListView_SelectionChanged;

            ShowPanel("goskey");

            _pollingTimer = new System.Windows.Threading.DispatcherTimer();
            _pollingTimer.Interval = TimeSpan.FromSeconds(30);
            _pollingTimer.Tick += PollingTimer_Tick;
            _pollingTimer.Start();
        }

        private void ShowPanel(string panel)
        {
            PanelGoskey.Visibility = panel == "goskey" ? Visibility.Visible : Visibility.Collapsed;
            PanelAddressBook.Visibility = panel == "addressbook" ? Visibility.Visible : Visibility.Collapsed;
            PanelSettings.Visibility = panel == "settings" ? Visibility.Visible : Visibility.Collapsed;

            ResetNavHighlight(BtnNavGoskey, panel == "goskey");
            ResetNavHighlight(BtnNavAddressBook, panel == "addressbook");
            ResetNavHighlight(BtnNavSettings, panel == "settings");

            TxtHeader.Text = panel switch
            {
                "addressbook" => "Адресная книга",
                "settings" => "Настройки",
                _ => "Отправка документов в Госключ"
            };
        }

        private static void ResetNavHighlight(Button button, bool active)
        {
            button.Background = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x6B, 0xA8))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x50, 0x82));
            button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        }

        private void NavGoskey_Click(object sender, RoutedEventArgs e) => ShowPanel("goskey");
        private void NavAddressBook_Click(object sender, RoutedEventArgs e) => ShowPanel("addressbook");
        private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowPanel("settings");

        private void ApplySettingsToUi()
        {
            var s = _settingsManager.Current;
            TxtSettingsStatementsDir.Text = s.StatementsDirectory;
            TxtSettingsDocsDir.Text = s.DefaultDocumentsDirectory;
            TxtApiKeyBox.Text = string.IsNullOrWhiteSpace(s.DefaultApiKey) ? AppDefaults.ApiKey : s.DefaultApiKey;
            TxtSettingsDefaultDescription.Text = s.DefaultDescription;
            TxtDescription.Text = string.IsNullOrWhiteSpace(s.DefaultDescription)
                ? "Членская книжка ТСН Сапожок"
                : s.DefaultDescription;
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtSettingsStatementsDir.Text))
            {
                MessageBox.Show("Укажите папку для сохранения заявлений.", "Настройки", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settingsManager.Current.StatementsDirectory = TxtSettingsStatementsDir.Text.Trim();
            _settingsManager.Current.DefaultDocumentsDirectory = TxtSettingsDocsDir.Text.Trim();
            _settingsManager.Current.DefaultApiKey = TxtApiKeyBox.Text.Trim();
            _settingsManager.Current.DefaultDescription = TxtSettingsDefaultDescription.Text.Trim();
            _settingsManager.Current.DefaultCertificateThumbprint =
                CertComboBox.SelectedValue?.ToString() ?? string.Empty;

            _settingsManager.SaveSettings();
            TxtDescription.Text = _settingsManager.Current.DefaultDescription;
            UpdateActiveCertSummary();

            MessageBox.Show("Настройки сохранены.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnBrowseStatementsDir_Click(object sender, RoutedEventArgs e)
        {
            string? folder = PickFolder("Выберите папку для заявлений", TxtSettingsStatementsDir.Text);
            if (folder != null)
                TxtSettingsStatementsDir.Text = folder;
        }

        private void BtnBrowseDocsDir_Click(object sender, RoutedEventArgs e)
        {
            string? folder = PickFolder("Выберите папку для документов по умолчанию", TxtSettingsDocsDir.Text);
            if (folder != null)
                TxtSettingsDocsDir.Text = folder;
        }

        private static string? PickFolder(string title, string initial)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }

        private void LoadAddressBook()
        {
            _addressBookEntries = AddressBookManager.Load()
                .OrderBy(e => e.LastName)
                .ThenBy(e => e.FirstName)
                .ToList();
            AddressBookGrid.ItemsSource = null;
            AddressBookGrid.ItemsSource = _addressBookEntries;

            CmbAddressBookPick.ItemsSource = null;
            CmbAddressBookPick.ItemsSource = _addressBookEntries;
        }

        private void AddressBookGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AddressBookGrid.SelectedItem is not AddressBookEntry entry)
                return;

            _editingAddressBookId = entry.Id;
            TxtAbLastName.Text = entry.LastName;
            TxtAbFirstName.Text = entry.FirstName;
            TxtAbPatronymic.Text = entry.Patronymic;
            TxtAbSnils.Text = entry.Snils;
        }

        private void BtnAbNew_Click(object sender, RoutedEventArgs e)
        {
            _editingAddressBookId = null;
            AddressBookGrid.SelectedItem = null;
            TxtAbLastName.Clear();
            TxtAbFirstName.Clear();
            TxtAbPatronymic.Clear();
            TxtAbSnils.Clear();
            TxtAbLastName.Focus();
        }

        private void BtnAbSave_Click(object sender, RoutedEventArgs e)
        {
            string lastName = TxtAbLastName.Text.Trim();
            if (string.IsNullOrWhiteSpace(lastName))
            {
                MessageBox.Show("Укажите фамилию.", "Адресная книга", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string snils = SnilsFormatter.Format(TxtAbSnils.Text);
            if (!SnilsFormatter.IsComplete(snils))
            {
                MessageBox.Show("СНИЛС должен содержать 11 цифр.", "Адресная книга", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AddressBookEntry entry;
            if (_editingAddressBookId.HasValue)
            {
                entry = _addressBookEntries.FirstOrDefault(x => x.Id == _editingAddressBookId.Value)
                        ?? new AddressBookEntry { Id = _editingAddressBookId.Value };
            }
            else
            {
                entry = new AddressBookEntry();
                _addressBookEntries.Add(entry);
            }

            entry.LastName = lastName;
            entry.FirstName = TxtAbFirstName.Text.Trim();
            entry.Patronymic = TxtAbPatronymic.Text.Trim();
            entry.Snils = snils;

            AddressBookManager.Save(_addressBookEntries);
            _editingAddressBookId = entry.Id;
            LoadAddressBook();
            AddressBookGrid.SelectedItem = _addressBookEntries.FirstOrDefault(x => x.Id == entry.Id);
        }

        private void BtnAbDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!_editingAddressBookId.HasValue)
            {
                MessageBox.Show("Выберите запись для удаления.", "Адресная книга", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show("Удалить выбранную запись?", "Адресная книга", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _addressBookEntries.RemoveAll(x => x.Id == _editingAddressBookId.Value);
            AddressBookManager.Save(_addressBookEntries);
            BtnAbNew_Click(sender, e);
            LoadAddressBook();
        }

        private void BtnAbUseForSend_Click(object sender, RoutedEventArgs e)
        {
            AddressBookEntry? entry = AddressBookGrid.SelectedItem as AddressBookEntry;
            if (entry == null && _editingAddressBookId.HasValue)
                entry = _addressBookEntries.FirstOrDefault(x => x.Id == _editingAddressBookId.Value);

            if (entry == null)
            {
                MessageBox.Show("Выберите или сохраните запись в адресной книге.", "Адресная книга", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyAddressEntryToSendForm(entry);
            ShowPanel("goskey");
        }

        private void CmbAddressBookPick_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbAddressBookPick.SelectedItem is AddressBookEntry entry)
                ApplyAddressEntryToSendForm(entry);
        }

        private void ApplyAddressEntryToSendForm(AddressBookEntry entry)
        {
            TxtSnils.Text = entry.Snils;
            if (string.IsNullOrWhiteSpace(TxtDescription.Text))
                TxtDescription.Text = _settingsManager.Current.DefaultDescription;
        }

        private void SignatureType_Checked(object sender, RoutedEventArgs e)
        {
            if (RbUnep != null && RbUnep.IsChecked == true)
                _selectedSignatureType = SignatureType.Unep;
            else
                _selectedSignatureType = SignatureType.Ukep;
        }

        private bool _isFormattingAbSnils;
        private void TxtAbSnils_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isFormattingAbSnils)
                return;

            string formatted = SnilsFormatter.Format(TxtAbSnils.Text);
            _isFormattingAbSnils = true;
            TxtAbSnils.Text = formatted;
            TxtAbSnils.SelectionStart = formatted.Length;
            _isFormattingAbSnils = false;
        }

        private void Log(string message)
        {
            TxtStatus.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            TxtStatus.ScrollToEnd();
        }

        private void LoadHistoryToUI()
        {
            // Preserve previously selected order id (if any)
            long? previouslySelected = _selectedOrderId;

            _ordersList = HistoryManager.LoadHistory();
            OrdersListView.ItemsSource = null;
            OrdersListView.ItemsSource = _ordersList;

            // If there was no selection yet, default to the most recently created order
            if (!previouslySelected.HasValue && _ordersList.Count > 0)
            {
                // Choose by CreatedAt descending to pick the freshest (last sent) record
                _selectedOrderId = _ordersList.OrderByDescending(o => o.CreatedAt).First().OrderId;
            }

            // Try to restore selection by id
            if (_selectedOrderId.HasValue)
            {
                OrderRecord? toSelect = _ordersList.FirstOrDefault(o => o.OrderId == _selectedOrderId.Value);
                if (toSelect != null)
                {
                    OrdersListView.SelectedItem = toSelect;
                    OrdersListView.ScrollIntoView(toSelect);
                }
            }
        }

        private void OrdersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OrdersListView.SelectedItem is OrderRecord selected)
                _selectedOrderId = selected.OrderId;
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

                string preferred = CryptoSigner.NormalizeThumbprint(_settingsManager.Current.DefaultCertificateThumbprint);
                var preferredCert = validCerts.FirstOrDefault(c =>
                    string.Equals(c.Thumbprint, preferred, StringComparison.OrdinalIgnoreCase));

                if (preferredCert != null)
                    CertComboBox.SelectedItem = preferredCert;
                else if (validCerts.Count > 0)
                    CertComboBox.SelectedIndex = 0;

                UpdateActiveCertSummary();
                Log($"Сертификатов с закрытым ключом: {validCerts.Count}");
            }
            catch (Exception ex)
            {
                Log($"Ошибка загрузки сертификатов: {FormatException(ex)}");
            }
        }

        private void CertComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateActiveCertSummary();

        private void UpdateActiveCertSummary()
        {
            if (CertComboBox.SelectedItem is CertificateItem cert)
            {
                TxtActiveCertSummary.Text =
                    $"{cert.SubjectName}\nОтпечаток: {cert.Thumbprint}\nДействует до: {cert.NotAfter:dd.MM.yyyy}";
            }
            else
            {
                TxtActiveCertSummary.Text = "Сертификат не выбран. Откройте «Настройки» и выберите сертификат по умолчанию.";
            }
        }

        private void RefreshCerts_Click(object sender, RoutedEventArgs e)
        {
            LoadCertificates();
        }

        private void BtnCertProperties_Click(object sender, RoutedEventArgs e)
        {
            if (CertComboBox.SelectedItem is not CertificateItem selected)
            {
                MessageBox.Show("Сначала выберите сертификат в настройках.", "Сертификат", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    $"Закрытый ключ CryptoPro: {(certificate.HasPrivateKey ? "доступен" : "недоступен")}\n" +
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
            string startDir = _settingsManager.Current.DefaultDocumentsDirectory;
            if (!Directory.Exists(startDir))
                startDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Поддерживаемые файлы (*.pdf;*.tif;*.tiff;*.jpg;*.jpeg;*.xml;*.txt)|*.pdf;*.tif;*.tiff;*.jpg;*.jpeg;*.xml;*.txt|Все файлы (*.*)|*.*",
                InitialDirectory = startDir,
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                _selectedFiles = dlg.FileNames.ToList();
                TxtFilePath.Text = string.Join("; ", _selectedFiles.Select(Path.GetFileName));
            }
        }

        private bool _isFormattingSnils;
        private void TxtSnils_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isFormattingSnils)
                return;

            string formatted = SnilsFormatter.Format(TxtSnils.Text);
            _isFormattingSnils = true;
            TxtSnils.Text = formatted;
            TxtSnils.SelectionStart = formatted.Length;
            _isFormattingSnils = false;
        }

        private async void PushArchiveSmartAsync_Click(object sender, RoutedEventArgs e)
        {
            if (CertComboBox.SelectedValue == null || _selectedFiles.Count == 0)
            {
                MessageBox.Show("Выберите сертификат в настройках и файл для отправки.", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!SnilsFormatter.IsComplete(TxtSnils.Text))
            {
                MessageBox.Show("Укажите корректный СНИЛС получателя (11 цифр).", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string thumbprint = CertComboBox.SelectedValue.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = string.Empty;
            SignatureType signatureType = _selectedSignatureType;

            try
            {
                Log($"--- Отправка {(_selectedFiles.Count == 1 ? "документа" : "пакета из " + _selectedFiles.Count + " документов")} в Госключ ({(signatureType == SignatureType.Unep ? "УНЭП" : "УКЭП")}) ---");
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

                Log(signatureType == SignatureType.Ukep
                    ? "Формирование XML УКЭП и CMS detached-подписей для всех файлов архива..."
                    : "Формирование XML УНЭП и обязательных CMS detached-подписей отправителя...");
                ArchiveBuildResult archive = _archiveBuilder.BuildArchive(_selectedFiles, TxtSnils.Text, TxtDescription.Text, thumbprint, signatureType);
                Log("CMS-подписи отправителя успешно сформированы и локально проверены.");

                long orderId = await _apiClient.SendPushAsync(archive.ArchiveBytes, token);

                string saveDir = Path.Combine(_settingsManager.Current.StatementsDirectory, orderId.ToString());
                Directory.CreateDirectory(saveDir);
                SaveArchiveMaterials(saveDir, archive.Files);

                var record = new OrderRecord
                {
                    OrderId = orderId,
                    FileName = string.Join(", ", _selectedFiles.Select(path => Path.GetFileName(path)!)),
                    SourceFiles = _selectedFiles.Select(path => Path.GetFileName(path)!).ToList(),
                    SignatureType = signatureType == SignatureType.Unep ? "УНЭП" : "УКЭП",
                    Snils = TxtSnils.Text,
                    Description = TxtDescription.Text,
                    CreatedAt = DateTime.Now,
                    StatusName = "Принята порталом, ожидается обработка"
                };

                _selectedOrderId = orderId;
                HistoryManager.AddOrUpdate(record);
                LoadHistoryToUI();

                Log($"Заявка принята порталом. Order ID: {orderId}. Это ещё не подтверждение доставки в Госключ.");
                Log("Проверка фактической доставки в Госключ...");
                OrderRecord tracked = FindOrder(orderId) ?? record;
                await RefreshOrderStatusAsync(tracked, token);
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
                MessageBox.Show("Выберите заявление из таблицы истории.", "История", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = string.Empty;

            try
            {
                Log($"Проверка статуса для Order ID: {selectedOrder.OrderId}...");
                if (string.IsNullOrEmpty(token))
                    token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                OrderRecord tracked = FindOrder(selectedOrder.OrderId) ?? selectedOrder;
                await RefreshOrderStatusAsync(tracked, token);
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
                MessageBox.Show("Выберите заявку в списке.", "Скачивание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            string token = string.Empty;

            try
            {
                Log($"Скачивание подписанных файлов для Order ID: {selectedOrder.OrderId}...");

                if (string.IsNullOrEmpty(token))
                    token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);

                OrderRecord tracked = FindOrder(selectedOrder.OrderId) ?? selectedOrder;
                await RefreshOrderStatusAsync(tracked, token);

                if (!IsDocumentsSigned(tracked.StatusName, tracked.StateOrgStatusCode))
                {
                    MessageBox.Show(
                        "Документы ещё не подписаны.\nТекущий статус: " + tracked.StatusName,
                        "Внимание",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string saveDir = Path.Combine(_settingsManager.Current.StatementsDirectory, tracked.OrderId.ToString());
                int downloaded = await DownloadSignedFilesAsync(tracked, token, saveDir);

                Log($"Скачивание завершено. Успешно: {downloaded}. Папка: {saveDir}");
                if (downloaded == 0)
                {
                    MessageBox.Show(
                        "Не удалось скачать файлы. Проверьте журнал операций.",
                        "Скачивание",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"Файлы подписей сохранены в папку:\n{saveDir}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка: {ex.Message}");
                MessageBox.Show($"Произошла ошибка при скачивании.\nДетали: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnDeleteOrder_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord order)
            {
                MessageBox.Show("Выберите заявку в списке.", "История", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show(
                    $"Удалить заявку {order.OrderId} только из списка истории?\nФайлы в папке результата останутся.",
                    "Удаление заявки",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            HistoryManager.Delete(order.OrderId);
            LoadHistoryToUI();
            Log($"Заявка {order.OrderId} удалена из истории.");
        }

        private void BtnVerifySignedFiles_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord order)
            {
                MessageBox.Show("Выберите заявку в списке.", "Проверка подписей", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string saveDir = Path.Combine(_settingsManager.Current.StatementsDirectory, order.OrderId.ToString());
            if (!Directory.Exists(saveDir))
            {
                MessageBox.Show("Папка результата заявки не найдена.", "Проверка подписей", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string receivedDir = Path.Combine(saveDir, "Госключ");
            string[] signatures = Directory.Exists(receivedDir)
                ? Directory.GetFiles(receivedDir, "*.sig", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();
            if (signatures.Length == 0)
            {
                MessageBox.Show("В папке «Госключ» нет скачанных подписей. Сначала скачайте результаты.", "Проверка подписей", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var report = new StringBuilder();
            int valid = 0;
            foreach (string signaturePath in signatures)
            {
                string signedFileName = Path.ChangeExtension(Path.GetFileName(signaturePath), null) ?? string.Empty;

                string origPath = Path.Combine(saveDir, signedFileName);
                string dlPath = Path.Combine(receivedDir, signedFileName);

                byte[] signatureBytes = File.ReadAllBytes(signaturePath);
                bool isVerified = false;

                // 1. Пробуем сверить с оригиналом
                if (File.Exists(origPath))
                {
                    byte[] origBytes = File.ReadAllBytes(origPath);
                    SignatureVerificationResult resOrig = CryptoSigner.VerifyDetachedSignature(origBytes, signatureBytes);
                    if (resOrig.IsValid)
                    {
                        valid++;
                        report.AppendLine($"✅ Документ: {signedFileName}");
                        report.AppendLine($"   {resOrig.Details}");
                        report.AppendLine(); // Пустая строка для визуального разделения
                        isVerified = true;
                    }
                }

                // 2. Если с оригиналом не сошлось - пробуем скачанный
                if (!isVerified && File.Exists(dlPath))
                {
                    byte[] dlBytes = File.ReadAllBytes(dlPath);
                    SignatureVerificationResult resDl = CryptoSigner.VerifyDetachedSignature(dlBytes, signatureBytes);
                    if (resDl.IsValid)
                    {
                        valid++;
                        report.AppendLine($"✅ Документ: {signedFileName} (из загрузок)");
                        report.AppendLine($"   {resDl.Details}");
                        report.AppendLine();
                        isVerified = true;
                    }
                    else
                    {
                        report.AppendLine($"❌ Документ: {signedFileName}");
                        report.AppendLine($"   Ошибка: {resDl.Details}");
                        report.AppendLine();
                    }
                }

                if (!File.Exists(origPath) && !File.Exists(dlPath))
                {
                    report.AppendLine($"⚠️ Документ: {signedFileName}");
                    report.AppendLine($"   Ошибка: Исходный файл не найден ни в одной из папок.");
                    report.AppendLine();
                }
            }

            string summary = $"Проверено подписей: {signatures.Length}; действительных: {valid}.";
            Log(summary);
            MessageBox.Show(summary + Environment.NewLine + Environment.NewLine + report, "Результаты проверки подписей", MessageBoxButton.OK,
                valid == signatures.Length ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private static void SaveArchiveMaterials(string saveDir, IReadOnlyDictionary<string, byte[]> files)
        {
            foreach ((string name, byte[] contents) in files)
            {
                string targetDir = name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(saveDir, "Отправитель")
                    : saveDir;
                Directory.CreateDirectory(targetDir);
                File.WriteAllBytes(Path.Combine(targetDir, Path.GetFileName(name)), contents);
            }
        }

        private OrderRecord? FindOrder(long orderId) =>
            _ordersList.FirstOrDefault(o => o.OrderId == orderId);

        private async Task RefreshOrderStatusAsync(OrderRecord order, string token)
        {
            string responseJson = await _apiClient.GetOrderStatusAsync(order.OrderId, token);
            OrderStatusParser.ApplyFromResponse(order, responseJson);

            order.IsCompleted = IsTerminalStatus(order.StatusName, order.StateOrgStatusCode);

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
                    $"; ID истории: {order.CurrentStatusHistoryId}; результат: {order.ResultFiles.Count} файл(ов).");
            }
        }

        private sealed record DownloadTarget(string Mnemonic, string ObjectType, string LocalFileName);

        private static List<DownloadTarget> BuildDownloadTargets(OrderRecord order)
        {
            var targets = new List<DownloadTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string mnemonic, string objectType, string localFileName)
            {
                string key = objectType + "|" + mnemonic;
                if (!seen.Add(key))
                    return;
                targets.Add(new DownloadTarget(mnemonic, objectType, localFileName));
            }

            foreach (ResultFileInfo file in order.ResultFiles)
            {
                try
                {
                    (string mnemonic, string objectType) = ApiClient.ParseAttachmentLink(file.Link);
                    // orderResponseFiles возвращает два разных результата:
                    // подпись (*.sig) и подписанный документ (*_attach).
                    // Нельзя дописывать .sig к последнему, иначе два файла
                    // получают один путь и документ не скачивается.
                    string localName = string.IsNullOrWhiteSpace(file.FileName) ? mnemonic : file.FileName;
                    Add(mnemonic, objectType, Path.Combine("Госключ", localName));
                }
                catch
                {
                    // ignore malformed links
                }
            }

            // When the status response contains file links, they are the
            // authoritative list.  Do not manufacture req.xml.sig or a
            // <source>.sig name: those names are not storage mnemonics and
            // produce misleading 404 errors.
            if (targets.Count == 0)
            {
                IEnumerable<string> sourceNames = order.SourceFiles.Count > 0
                    ? order.SourceFiles
                    : new[] { order.FileName };
                foreach (string sourceName in sourceNames.Where(name => !string.IsNullOrWhiteSpace(name)))
                {
                    string localName = sourceName.EndsWith(".sig", StringComparison.OrdinalIgnoreCase)
                        ? sourceName
                        : sourceName + ".sig";
                    // The download API accepts the original document mnemonic
                    // and returns its detached signature. This is only a
                    // fallback for old responses without result links.
                    Add(sourceName, "3", Path.Combine("Госключ", localName));
                }
            }

            return targets;
        }

        private async Task<int> DownloadSignedFilesAsync(OrderRecord order, string token, string saveDir)
        {
            if (order.CurrentStatusHistoryId <= 0)
            {
                Log("Скачивание невозможно: сервер ещё не выдал ID статуса заявки.");
                return 0;
            }

            Directory.CreateDirectory(saveDir);
            int downloaded = 0;

            foreach (DownloadTarget target in BuildDownloadTargets(order))
            {
                string savePath = Path.Combine(saveDir, target.LocalFileName);
                string? targetDirectory = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);
                if (File.Exists(savePath))
                {
                    downloaded++;
                    continue;
                }

                try
                {
                    await _apiClient.DownloadResultFileAsync(
                        order.CurrentStatusHistoryId,
                        target.Mnemonic,
                        token,
                        savePath,
                        target.ObjectType);
                    Log($"Успешно скачан файл: {target.LocalFileName}");
                    downloaded++;
                }
                catch (Exception ex)
                {
                    Log($"Не удалось скачать {target.Mnemonic}: {ex.Message}");
                }
            }

            return downloaded;
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

        private static bool IsDocumentsSigned(string? statusName, string? stateOrgStatusCode) =>
            (!string.IsNullOrWhiteSpace(stateOrgStatusCode) && stateOrgStatusCode.Contains("SIGNED", StringComparison.OrdinalIgnoreCase))
            || IsDocumentsSigned(statusName);

        private static bool IsTerminalStatus(string? statusName, string? stateOrgStatusCode)
        {
            if (!string.IsNullOrWhiteSpace(stateOrgStatusCode) &&
                (stateOrgStatusCode.Contains("DONE", StringComparison.OrdinalIgnoreCase) ||
                 stateOrgStatusCode.Contains("SIGNED", StringComparison.OrdinalIgnoreCase) ||
                 stateOrgStatusCode.Contains("REJECT", StringComparison.OrdinalIgnoreCase) ||
                 stateOrgStatusCode.Contains("INVALID", StringComparison.OrdinalIgnoreCase) ||
                 stateOrgStatusCode.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                 stateOrgStatusCode.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(statusName) &&
                   (statusName.Contains("оказана", StringComparison.OrdinalIgnoreCase) ||
                    statusName.Contains("подписан", StringComparison.OrdinalIgnoreCase) ||
                    statusName.Contains("отказ", StringComparison.OrdinalIgnoreCase) ||
                    statusName.Contains("недействител", StringComparison.OrdinalIgnoreCase) ||
                    IsDeliveryFailure(statusName));
        }

        private bool _isPolling;
        private async void PollingTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPolling)
                return;
            _isPolling = true;

            try
            {
                var incompleteOrderIds = _ordersList.Where(o => !o.IsCompleted).Select(o => o.OrderId).ToList();
                if (incompleteOrderIds.Count == 0)
                    return;

                string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
                string apiKey = TxtApiKeyBox.Text.Trim();
                string token = string.Empty;

                if (string.IsNullOrEmpty(token))
                {
                    try
                    {
                        token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Фон] Ошибка получения токена: {ex.Message}");
                        return;
                    }
                }

                foreach (long orderId in incompleteOrderIds)
                {
                    OrderRecord? order = FindOrder(orderId);
                    if (order == null)
                        continue;

                    try
                    {
                        Log($"[Фон] Опрос статуса заявки {order.OrderId}...");
                        await RefreshOrderStatusAsync(order, token);

                        order = FindOrder(orderId) ?? order;
                        if (IsDocumentsSigned(order.StatusName, order.StateOrgStatusCode))
                            await AutoDownloadSignedFilesAsync(order, token);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Фон] Ошибка при опросе {orderId}: {ex.Message}");
                    }
                }
            }
            finally
            {
                _isPolling = false;
            }
        }

        private async Task AutoDownloadSignedFilesAsync(OrderRecord order, string token)
        {
            string saveDir = Path.Combine(_settingsManager.Current.StatementsDirectory, order.OrderId.ToString());
            Log($"[Фон] Скачивание подписей для заявки {order.OrderId}...");
            int downloaded = await DownloadSignedFilesAsync(order, token, saveDir);
            if (downloaded > 0)
                Log($"[Фон] Скачано файлов: {downloaded}. Папка: {saveDir}");
        }
    }
}

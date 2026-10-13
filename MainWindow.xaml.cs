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
            PanelFssp.Visibility = panel == "fssp" ? Visibility.Visible : Visibility.Collapsed;

            ResetNavHighlight(BtnNavGoskey, panel == "goskey");
            ResetNavHighlight(BtnNavAddressBook, panel == "addressbook");
            ResetNavHighlight(BtnNavSettings, panel == "settings");
            ResetNavHighlight(BtnNavFssp, panel == "fssp");

            TxtHeader.Text = panel switch
            {
                "addressbook" => "Адресная книга",
                "settings" => "Настройки",
                "fssp" => "Предоставление информации из ФССП",
                _ => "Отправка документов в Госключ"
            };
        }

        private static void ResetNavHighlight(Button button, bool active)
        {
            if (button == null) return;

            button.Background = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x6B, 0xA8))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x50, 0x82));
            button.FontWeight = active ? FontWeights.Bold : FontWeights.Normal;
        }

        private void NavGoskey_Click(object sender, RoutedEventArgs e) => ShowPanel("goskey");
        private void NavAddressBook_Click(object sender, RoutedEventArgs e) => ShowPanel("addressbook");
        private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowPanel("settings");
        private void NavFssp_Click(object sender, RoutedEventArgs e) => ShowPanel("fssp");

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

            // НОВЫЕ СТРОКИ: Автозаполнение полей ФССП
            TxtFsspDeloNum.Text = s.FsspDeloNum;
            TxtFsspSnils.Text = s.FsspSnils;
            TxtFsspFullName.Text = s.FsspFullName;
            TxtFsspBirthDate.Text = s.FsspBirthDate;
            TxtFsspCompName.Text = string.IsNullOrWhiteSpace(s.FsspCompName) ? "ТСН «Сапожок»" : s.FsspCompName;
            TxtFsspCompInn.Text = s.FsspCompInn;
            TxtFsspCompOgrn.Text = s.FsspCompOgrn;
            TxtFsspCompAddress.Text = s.FsspCompAddress;
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
            _settingsManager.Current.DefaultCertificateThumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;

            _settingsManager.SaveSettings();
            TxtDescription.Text = _settingsManager.Current.DefaultDescription;
            UpdateActiveCertSummary();

            MessageBox.Show("Настройки сохранены.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnBrowseStatementsDir_Click(object sender, RoutedEventArgs e)
        {
            string? folder = PickFolder("Выберите папку для заявлений", TxtSettingsStatementsDir.Text);
            if (folder != null) TxtSettingsStatementsDir.Text = folder;
        }

        private void BtnBrowseDocsDir_Click(object sender, RoutedEventArgs e)
        {
            string? folder = PickFolder("Выберите папку для документов по умолчанию", TxtSettingsDocsDir.Text);
            if (folder != null) TxtSettingsDocsDir.Text = folder;
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
            if (AddressBookGrid.SelectedItem is not AddressBookEntry entry) return;

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
                entry = _addressBookEntries.FirstOrDefault(x => x.Id == _editingAddressBookId.Value) ?? new AddressBookEntry { Id = _editingAddressBookId.Value };
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
            TxtGoskeySnils.Text = entry.Snils;
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
            if (_isFormattingAbSnils) return;
            string formatted = SnilsFormatter.Format(TxtAbSnils.Text);
            _isFormattingAbSnils = true;
            TxtAbSnils.Text = formatted;
            TxtAbSnils.SelectionStart = formatted.Length;
            _isFormattingAbSnils = false;
        }

        private bool _isFormattingSnils;
        private void TxtSnils_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isFormattingSnils) return;
            string formatted = SnilsFormatter.Format(TxtGoskeySnils.Text);
            _isFormattingSnils = true;
            TxtGoskeySnils.Text = formatted;
            TxtGoskeySnils.SelectionStart = formatted.Length;
            _isFormattingSnils = false;
        }

        private void Log(string message)
        {
            TxtStatus.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            TxtStatus.ScrollToEnd();
        }

        private void LoadHistoryToUI()
        {
            long? previouslySelected = _selectedOrderId;
            _ordersList = HistoryManager.LoadHistory();

            // Госключ
            OrdersListView.ItemsSource = _ordersList.Where(o => o.SignatureType != "ФССП").ToList();

            // ФССП
            if (FsspOrdersListView != null)
                FsspOrdersListView.ItemsSource = _ordersList.Where(o => o.SignatureType == "ФССП").ToList();

            if (!previouslySelected.HasValue && _ordersList.Count > 0)
                _selectedOrderId = _ordersList.OrderByDescending(o => o.CreatedAt).First().OrderId;

            if (_selectedOrderId.HasValue)
            {
                OrderRecord? toSelect = _ordersList.FirstOrDefault(o => o.OrderId == _selectedOrderId.Value);
                if (toSelect != null)
                {
                    if (toSelect.SignatureType == "ФССП" && FsspOrdersListView != null)
                        FsspOrdersListView.SelectedItem = toSelect;
                    else
                        OrdersListView.SelectedItem = toSelect;
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

                        byThumbprint[thumbprint] = new CertificateItem(ParseCommonName(certificate.Subject), thumbprint, location.ToString(), certificate.NotAfter);
                    }
                }

                var validCerts = byThumbprint.Values.OrderByDescending(c => c.NotAfter).ToList();
                CertComboBox.ItemsSource = validCerts;

                string preferred = CryptoSigner.NormalizeThumbprint(_settingsManager.Current.DefaultCertificateThumbprint);
                var preferredCert = validCerts.FirstOrDefault(c => string.Equals(c.Thumbprint, preferred, StringComparison.OrdinalIgnoreCase));

                if (preferredCert != null) CertComboBox.SelectedItem = preferredCert;
                else if (validCerts.Count > 0) CertComboBox.SelectedIndex = 0;

                UpdateActiveCertSummary();
                Log($"Сертификатов с закрытым ключом: {validCerts.Count}");
            }
            catch (Exception ex)
            {
                Log($"Ошибка загрузки сертификатов: {FormatException(ex)}");
            }
        }

        private void CertComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActiveCertSummary();

        private void UpdateActiveCertSummary()
        {
            if (CertComboBox.SelectedItem is CertificateItem cert)
                TxtActiveCertSummary.Text = $"{cert.SubjectName}\nОтпечаток: {cert.Thumbprint}\nДействует до: {cert.NotAfter:dd.MM.yyyy}";
            else
                TxtActiveCertSummary.Text = "Сертификат не выбран. Откройте «Настройки» и выберите сертификат по умолчанию.";
        }

        private void RefreshCerts_Click(object sender, RoutedEventArgs e) => LoadCertificates();

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
                    $"Сертификат: {selected.SubjectName}\nОтпечаток: {selected.Thumbprint}\nХранилище: {selected.StoreLocation}\nСрок действия до: {certificate.NotAfter:dd.MM.yyyy HH:mm:ss}\nЗакрытый ключ CryptoPro: {(certificate.HasPrivateKey ? "доступен" : "недоступен")}\nАлгоритм: {certificate.PublicKey.Oid.Value}",
                    "Свойства сертификата", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(FormatException(ex), "Сертификат не готов к подписи", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private sealed record CertificateItem(string SubjectName, string Thumbprint, string StoreLocation, DateTime NotAfter);

        private static string FormatException(Exception ex)
        {
            var parts = new List<string>();
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(current.Message)) parts.Add(current.Message);
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
            if (!Directory.Exists(startDir)) startDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

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

        private async void PushArchiveSmartAsync_Click(object sender, RoutedEventArgs e)
        {
            if (CertComboBox.SelectedValue == null || _selectedFiles.Count == 0)
            {
                MessageBox.Show("Выберите сертификат в настройках и файл для отправки.", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!SnilsFormatter.IsComplete(TxtGoskeySnils.Text))
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

                ArchiveBuildResult archive = _archiveBuilder.BuildArchive(_selectedFiles, TxtGoskeySnils.Text, TxtDescription.Text, thumbprint, signatureType);
                Log("CMS-подписи отправителя успешно сформированы.");

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
                    Snils = TxtGoskeySnils.Text,
                    Description = TxtDescription.Text,
                    CreatedAt = DateTime.Now,
                    StatusName = "Принята порталом, ожидается обработка"
                };

                _selectedOrderId = orderId;
                HistoryManager.AddOrUpdate(record);
                LoadHistoryToUI();

                Log($"Заявка принята порталом. Order ID: {orderId}.");
                OrderRecord tracked = FindOrder(orderId) ?? record;
                await RefreshOrderStatusAsync(tracked, token);
            }
            catch (Exception ex)
            {
                Log($"ОШИБКА: {FormatException(ex)}");
                MessageBox.Show(FormatException(ex), "Ошибка отправки", MessageBoxButton.OK, MessageBoxImage.Error);
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
            try
            {
                Log($"Проверка статуса для Order ID: {selectedOrder.OrderId}...");
                string token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                OrderRecord tracked = FindOrder(selectedOrder.OrderId) ?? selectedOrder;
                await RefreshOrderStatusAsync(tracked, token);
            }
            catch (Exception ex) { Log($"Ошибка проверки статуса: {ex.Message}"); }
        }

        private async void BtnDownloadSignedFiles_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord selectedOrder) return;

            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();
            try
            {
                string token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                OrderRecord tracked = FindOrder(selectedOrder.OrderId) ?? selectedOrder;
                await RefreshOrderStatusAsync(tracked, token);

                if (!IsDocumentsSigned(tracked.StatusName, tracked.StateOrgStatusCode))
                {
                    MessageBox.Show("Документы ещё не подписаны.\nТекущий статус: " + tracked.StatusName, "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string saveDir = Path.Combine(_settingsManager.Current.StatementsDirectory, tracked.OrderId.ToString());
                int downloaded = await DownloadSignedFilesAsync(tracked, token, saveDir);

                if (downloaded > 0) MessageBox.Show($"Файлы сохранены в:\n{saveDir}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Log($"Ошибка: {ex.Message}"); }
        }

        private void BtnDeleteOrder_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord order) return;
            if (MessageBox.Show($"Удалить заявку {order.OrderId} из истории?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            HistoryManager.Delete(order.OrderId);
            LoadHistoryToUI();
        }

        private void BtnVerifySignedFiles_Click(object sender, RoutedEventArgs e)
        {
            if (OrdersListView.SelectedItem is not OrderRecord order) return;
            string saveDir = Path.Combine(_settingsManager.Current.StatementsDirectory, order.OrderId.ToString());
            string receivedDir = Path.Combine(saveDir, "Госключ");
            string[] signatures = Directory.Exists(receivedDir) ? Directory.GetFiles(receivedDir, "*.sig") : Array.Empty<string>();

            if (signatures.Length == 0)
            {
                MessageBox.Show("В папке нет скачанных подписей.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Information);
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

                if (File.Exists(origPath))
                {
                    var resOrig = CryptoSigner.VerifyDetachedSignature(File.ReadAllBytes(origPath), signatureBytes);
                    if (resOrig.IsValid) { valid++; report.AppendLine($"✅ {signedFileName}"); isVerified = true; }
                }

                if (!isVerified && File.Exists(dlPath))
                {
                    var resDl = CryptoSigner.VerifyDetachedSignature(File.ReadAllBytes(dlPath), signatureBytes);
                    if (resDl.IsValid) { valid++; report.AppendLine($"✅ {signedFileName} (загружено)"); isVerified = true; }
                }

                if (!isVerified) report.AppendLine($"❌ Ошибка проверки для: {signedFileName}");
            }
            MessageBox.Show(report.ToString(), $"Проверено: {valid}/{signatures.Length}", MessageBoxButton.OK, valid == signatures.Length ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private static void SaveArchiveMaterials(string saveDir, IReadOnlyDictionary<string, byte[]> files)
        {
            foreach ((string name, byte[] contents) in files)
            {
                string targetDir = name.EndsWith(".sig", StringComparison.OrdinalIgnoreCase) ? Path.Combine(saveDir, "Отправитель") : saveDir;
                Directory.CreateDirectory(targetDir);
                File.WriteAllBytes(Path.Combine(targetDir, Path.GetFileName(name)), contents);
            }
        }

        private OrderRecord? FindOrder(long orderId) => _ordersList.FirstOrDefault(o => o.OrderId == orderId);

        private async Task RefreshOrderStatusAsync(OrderRecord order, string token)
        {
            string responseJson = await _apiClient.GetOrderStatusAsync(order.OrderId, token);
            OrderStatusParser.ApplyFromResponse(order, responseJson);
            order.IsCompleted = IsTerminalStatus(order.StatusName, order.StateOrgStatusCode);
            HistoryManager.AddOrUpdate(order);
            LoadHistoryToUI();
        }

        private sealed record DownloadTarget(string Mnemonic, string ObjectType, string LocalFileName);

        private static List<DownloadTarget> BuildDownloadTargets(OrderRecord order)
        {
            var targets = new List<DownloadTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string mnemonic, string objectType, string localFileName)
            {
                if (seen.Add(objectType + "|" + mnemonic)) targets.Add(new DownloadTarget(mnemonic, objectType, localFileName));
            }

            foreach (ResultFileInfo file in order.ResultFiles)
            {
                try
                {
                    (string mnemonic, string objectType) = ApiClient.ParseAttachmentLink(file.Link);
                    string localName = string.IsNullOrWhiteSpace(file.FileName) ? mnemonic : file.FileName;
                    Add(mnemonic, objectType, Path.Combine("Госключ", localName));
                }
                catch { }
            }
            return targets;
        }

        private async Task<int> DownloadSignedFilesAsync(OrderRecord order, string token, string saveDir)
        {
            if (order.CurrentStatusHistoryId <= 0) return 0;
            Directory.CreateDirectory(saveDir);
            int downloaded = 0;

            foreach (DownloadTarget target in BuildDownloadTargets(order))
            {
                string savePath = Path.Combine(saveDir, target.LocalFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
                if (File.Exists(savePath)) { downloaded++; continue; }

                try
                {
                    await _apiClient.DownloadResultFileAsync(order.CurrentStatusHistoryId, target.Mnemonic, token, savePath, target.ObjectType);
                    downloaded++;
                }
                catch { }
            }
            return downloaded;
        }

        private static bool IsDeliveryFailure(string? statusName) =>
            !string.IsNullOrWhiteSpace(statusName) && (statusName.Contains("не найдена", StringComparison.OrdinalIgnoreCase) || statusName.Contains("отклон", StringComparison.OrdinalIgnoreCase) || statusName.Contains("ошиб", StringComparison.OrdinalIgnoreCase) || statusName.Contains("истек", StringComparison.OrdinalIgnoreCase));

        private static bool IsDocumentsSigned(string? statusName) =>
            !string.IsNullOrWhiteSpace(statusName) && statusName.Contains("подписан", StringComparison.OrdinalIgnoreCase) && !IsDeliveryFailure(statusName);

        private static bool IsDocumentsSigned(string? statusName, string? stateOrgStatusCode) =>
            (!string.IsNullOrWhiteSpace(stateOrgStatusCode) && stateOrgStatusCode.Contains("SIGNED", StringComparison.OrdinalIgnoreCase)) || IsDocumentsSigned(statusName);

        private static bool IsTerminalStatus(string? statusName, string? stateOrgStatusCode)
        {
            if (!string.IsNullOrWhiteSpace(stateOrgStatusCode) && (stateOrgStatusCode.Contains("DONE", StringComparison.OrdinalIgnoreCase) || stateOrgStatusCode.Contains("SIGNED", StringComparison.OrdinalIgnoreCase) || stateOrgStatusCode.Contains("REJECT", StringComparison.OrdinalIgnoreCase) || stateOrgStatusCode.Contains("INVALID", StringComparison.OrdinalIgnoreCase) || stateOrgStatusCode.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || stateOrgStatusCode.Contains("EXPIRED", StringComparison.OrdinalIgnoreCase))) return true;
            return !string.IsNullOrWhiteSpace(statusName) && (statusName.Contains("оказана", StringComparison.OrdinalIgnoreCase) || statusName.Contains("подписан", StringComparison.OrdinalIgnoreCase) || statusName.Contains("отказ", StringComparison.OrdinalIgnoreCase) || statusName.Contains("недействител", StringComparison.OrdinalIgnoreCase) || IsDeliveryFailure(statusName));
        }

        private bool _isPolling;
        private async void PollingTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPolling) return;
            _isPolling = true;

            try
            {
                var incompleteOrderIds = _ordersList.Where(o => !o.IsCompleted).Select(o => o.OrderId).ToList();
                if (incompleteOrderIds.Count == 0) return;

                string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
                string apiKey = TxtApiKeyBox.Text.Trim();
                string token = string.Empty;

                foreach (long orderId in incompleteOrderIds)
                {
                    OrderRecord? order = FindOrder(orderId);
                    if (order == null) continue;

                    try
                    {
                        if (string.IsNullOrEmpty(token)) token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);
                        await RefreshOrderStatusAsync(order, token);
                    }
                    catch { }
                }
            }
            finally { _isPolling = false; }
        }

        // ==========================================
        // МЕТОДЫ ФССП
        // ==========================================

        private void CmbFsspRequestType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PanelFsspDeloNum == null) return;
            var selectedItem = CmbFsspRequestType.SelectedItem as System.Windows.Controls.ComboBoxItem;
            string requestTypeStr = selectedItem?.Tag?.ToString() ?? "CourseIP";

            if (requestTypeStr == "ExistIP")
            {
                PanelFsspDeloNum.Visibility = Visibility.Collapsed;
                TxtFsspDeloNum.Clear();
            }
            else
            {
                PanelFsspDeloNum.Visibility = Visibility.Visible;
            }
        }

        private void CmbFsspApplicantType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PanelFsspLegalEntity == null) return;
            var selectedItem = CmbFsspApplicantType.SelectedItem as System.Windows.Controls.ComboBoxItem;
            PanelFsspLegalEntity.Visibility = (selectedItem?.Tag?.ToString() == "UL") ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnFsspSend_Click(object sender, RoutedEventArgs e)
        {
            BtnFsspSend.IsEnabled = false;

            var selectedItem = CmbFsspRequestType.SelectedItem as System.Windows.Controls.ComboBoxItem;
            string requestTypeStr = selectedItem?.Tag?.ToString() ?? "CourseIP";
            var requestType = requestTypeStr == "ExistIP" ? FsspRequestType.ExistIP : FsspRequestType.CourseIP;

            TxtFsspStatusLog.Text = $"[{DateTime.Now:HH:mm:ss}] Запуск формирования запроса в ФССП ({(requestType == FsspRequestType.ExistIP ? "Наличие ИП" : "Ход ИП")})...\n";

            try
            {
                string deloNum = TxtFsspDeloNum.Text.Trim();
                string snils = TxtFsspSnils.Text.Trim();
                string fullName = TxtFsspFullName.Text.Trim();
                string birthDate = TxtFsspBirthDate.Text.Trim();

                var complainerItem = CmbFsspComplainerType.SelectedItem as System.Windows.Controls.ComboBoxItem;
                string complainerType = complainerItem?.Tag?.ToString() ?? "2";

                var genderItem = CmbFsspGender.SelectedItem as System.Windows.Controls.ComboBoxItem;
                string gender = genderItem?.Tag?.ToString() ?? "1";

                var applicantItem = CmbFsspApplicantType.SelectedItem as System.Windows.Controls.ComboBoxItem;
                bool isLegalEntity = applicantItem?.Tag?.ToString() == "UL";

                string compName = TxtFsspCompName?.Text?.Trim() ?? "";
                string compInn = TxtFsspCompInn?.Text?.Trim() ?? "";
                string compOgrn = TxtFsspCompOgrn?.Text?.Trim() ?? "";
                string compAddress = TxtFsspCompAddress?.Text?.Trim() ?? "";

                // ДОБАВИТЬ ЭТОТ БЛОК: Автосохранение введенных данных
                var s = _settingsManager.Current;
                s.FsspDeloNum = deloNum;
                s.FsspSnils = snils;
                s.FsspFullName = fullName;
                s.FsspBirthDate = birthDate;
                s.FsspCompName = compName;
                s.FsspCompInn = compInn;
                s.FsspCompOgrn = compOgrn;
                s.FsspCompAddress = compAddress;
                _settingsManager.SaveSettings();
                // ----------------------------------------------------

                if (string.IsNullOrEmpty(snils) || string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(birthDate) ||
                    (requestType == FsspRequestType.CourseIP && string.IsNullOrEmpty(deloNum)))
                {
                    TxtFsspStatusLog.Text += "Ошибка: Заполните все обязательные поля.\n";
                    return;
                }

                var apiClient = new ApiClient();
                var fsspService = new FsspService(apiClient);

                string apiKey = TxtApiKeyBox.Text.Trim();
                string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;

                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Получение токена доступа...\n";
                string token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);

                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Резервирование номера заявления...\n";

                string result = await fsspService.SendFSSpRequestAsync(
                    requestType, deloNum, snils, fullName, birthDate, complainerType, gender, token,
                    isLegalEntity, compName, compInn, compOgrn, compAddress,
                    (req, piev) =>
                    {
                        TxtFsspStatusLog.Text += $"\n--- ОТПРАВЛЯЕМЫЙ req.xml ---\n{req}\n\n--- ОТПРАВЛЯЕМЫЙ piev_epgu.xml ---\n{piev}\n----------------------------\n";
                    });

                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] {result}\n";
                TxtFsspStatusLog.ScrollToEnd();

                if (result.Contains("OrderId: "))
                {
                    string orderIdStr = result.Substring(result.IndexOf("OrderId: ") + 9).Trim();
                    if (long.TryParse(orderIdStr, out long orderId))
                    {
                        var record = new OrderRecord
                        {
                            OrderId = orderId,
                            FileName = requestType == FsspRequestType.ExistIP ? "Наличие ИП" : "Ход ИП",
                            SignatureType = "ФССП",
                            Snils = snils,
                            CreatedAt = DateTime.Now,
                            StatusName = "Заявление отправлено в ведомство",
                            StateOrgStatusCode = "21"
                        };
                        HistoryManager.AddOrUpdate(record);
                        LoadHistoryToUI();
                    }
                }
            }
            catch (Exception ex)
            {
                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Возникла ошибка: {ex.Message}\n";
                TxtFsspStatusLog.ScrollToEnd();
            }
            finally
            {
                BtnFsspSend.IsEnabled = true;
            }
        }

        private async void BtnCheckFsspStatus_Click(object sender, RoutedEventArgs e)
        {
            if (FsspOrdersListView.SelectedItem is not OrderRecord selectedOrder)
            {
                MessageBox.Show("Выберите заявку ФССП в списке.", "История", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();

            try
            {
                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Проверка статуса для Order ID: {selectedOrder.OrderId}...\n";
                string token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);

                OrderRecord tracked = FindOrder(selectedOrder.OrderId) ?? selectedOrder;
                await RefreshOrderStatusAsync(tracked, token);

                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Обновлен статус: {tracked.StatusName} (Код: {tracked.StateOrgStatusCode})\n";
                TxtFsspStatusLog.ScrollToEnd();
            }
            catch (Exception ex)
            {
                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Ошибка проверки: {ex.Message}\n";
                TxtFsspStatusLog.ScrollToEnd();
            }
        }

        private async void BtnDownloadFsspResult_Click(object sender, RoutedEventArgs e)
        {
            if (FsspOrdersListView.SelectedItem is not OrderRecord selectedOrder)
            {
                MessageBox.Show("Выберите заявку ФССП в списке.", "Скачивание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string thumbprint = CertComboBox.SelectedValue?.ToString() ?? string.Empty;
            string apiKey = TxtApiKeyBox.Text.Trim();

            try
            {
                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Скачивание ответа для {selectedOrder.OrderId}...\n";
                string token = await _authManager.GetAccessTokenAsync(apiKey, thumbprint);

                OrderRecord tracked = FindOrder(selectedOrder.OrderId) ?? selectedOrder;
                await RefreshOrderStatusAsync(tracked, token);

                if (tracked.StateOrgStatusCode != "3" && !tracked.StatusName.Contains("оказана", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"Ответ от ФССП еще не сформирован или получен отказ.\nТекущий статус: {tracked.StatusName}", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string saveDir = Path.Combine(_settingsManager.Current.StatementsDirectory, "ФССП", tracked.OrderId.ToString());
                int downloaded = await DownloadSignedFilesAsync(tracked, token, saveDir);

                if (downloaded > 0) MessageBox.Show($"Файлы ответа успешно сохранены в папку:\n{saveDir}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                else MessageBox.Show("Не удалось найти файлы для скачивания. Возможно, архив еще не прикрепился на сервере ЕПГУ.", "Скачивание", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Ошибка скачивания: {ex.Message}\n";
                TxtFsspStatusLog.ScrollToEnd();
            }
        }

        private void BtnDeleteFsspOrder_Click(object sender, RoutedEventArgs e)
        {
            if (FsspOrdersListView.SelectedItem is not OrderRecord order) return;

            if (MessageBox.Show($"Удалить заявку {order.OrderId} из истории ФССП?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                HistoryManager.Delete(order.OrderId);
                LoadHistoryToUI();
                TxtFsspStatusLog.Text += $"[{DateTime.Now:HH:mm:ss}] Заявка {order.OrderId} удалена.\n";
                TxtFsspStatusLog.ScrollToEnd();
            }
        }
    }
}
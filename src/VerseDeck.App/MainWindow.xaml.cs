using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using VerseDeck.Core.Models;
using VerseDeck.Data;
using VerseDeck.Input;
using VerseDeck.MobileServer;
using VerseDeck.Voice;

namespace VerseDeck.App;

public partial class MainWindow : Window
{
    private static readonly string[] ModuleCategories = ["Flight", "Navigation", "Scan", "Combat", "Utility", "Emergency", "Systems", "Custom"];
    private static readonly string[] ModuleIcons = ["power", "engine", "gear", "flight", "quantum", "map", "radar", "scan", "shield", "weapons", "cargo", "comms", "doors", "eject", "warning", "lights"];
    private static readonly string[] ModuleFrames = ["Cyan", "Green", "Red"];

    private readonly SqliteVerseDeckRepository _repository;
    private readonly WindowsInputSender _inputSender = new();
    private readonly WindowsSpeechCommandService _voiceService = new();
    private readonly PttInputMonitor _pttMonitor = new();
    private readonly MediaPlayer _welcomePlayer = new();
    private readonly MediaPlayer _commandPlayer = new();
    private readonly ObservableCollection<DeckButtonTile> _buttonTiles = new();
    private readonly ObservableCollection<string> _logItems = new();
    private readonly ObservableCollection<string> _voicePhraseItems = new();
    private readonly ObservableCollection<Profile> _profiles = new();
    private readonly string _logPath;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _pttDetectTimer = new();
    private readonly DispatcherTimer _pttPauseTimer = new();
    private bool _pttDetectWaitingForRelease;
    private MobilePanelServer? _mobileServer;
    private Profile? _activeProfile;
    private DeckButton? _selectedButton;
    private IReadOnlyList<DeckButton> _buttons = [];
    private IReadOnlyList<VoiceCommand> _voiceCommands = [];
    private AppSettings _settings = new(4785, "2468", false, "ColdBlue", 0.40);

    public MainWindow()
    {
        InitializeComponent();
        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VerseDeck Companion");
        Directory.CreateDirectory(appDataPath);
        var dataPath = Path.Combine(appDataPath, "versedeck.db");
        _logPath = Path.Combine(appDataPath, "debug.log");
        _repository = new SqliteVerseDeckRepository(dataPath);
        DeckItems.DataContext = _buttonTiles;
        ProfileSelectComboBox.ItemsSource = _profiles;
        SelectedCategoryComboBox.ItemsSource = ModuleCategories;
        SelectedIconComboBox.ItemsSource = ModuleIcons;
        SelectedFrameComboBox.ItemsSource = ModuleFrames;
        CommandLogList.ItemsSource = _logItems;
        VoicePhrasesList.ItemsSource = _voicePhraseItems;
        _voiceService.CommandRecognized += VoiceService_CommandRecognized;
        _voiceService.Diagnostic += (_, message) => WriteDebug($"Voice: {message}");
        _pttMonitor.PressedChanged += PttMonitor_PressedChanged;
        _refreshTimer.Interval = TimeSpan.FromSeconds(2);
        _refreshTimer.Tick += async (_, _) => await RefreshLiveStatusAsync();
        _pttDetectTimer.Interval = TimeSpan.FromMilliseconds(35);
        _pttDetectTimer.Tick += PttDetectTimer_Tick;
        _pttPauseTimer.Interval = TimeSpan.FromSeconds(2.5);
        _pttPauseTimer.Tick += PttPauseTimer_Tick;
        LoadWindowIcon();
        LoadAudioPlayers();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartBackgroundVideo();
        await InitializeAsync();
    }

    private void BackgroundVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        StartBackgroundVideo();
    }

    private void BackgroundVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        WriteDebug($"Background video failed: {e.ErrorException}");
        BackgroundVideo.Visibility = Visibility.Collapsed;
    }

    private void StartBackgroundVideo()
    {
        try
        {
            BackgroundVideo.Position = TimeSpan.Zero;
            BackgroundVideo.Play();
        }
        catch (Exception ex)
        {
            WriteDebug($"Background video playback failed: {ex}");
            BackgroundVideo.Visibility = Visibility.Collapsed;
        }
    }

    private async Task InitializeAsync()
    {
        await _repository.InitializeAsync();
        _settings = await _repository.GetSettingsAsync();
        _mobileServer = CreateMobileServer();
        ShipNameComboBox.ItemsSource = ShipCatalog.Names;
        await LoadStateAsync();
        _refreshTimer.Start();
        WriteDebug("App initialized");
        StatusText.Text = "Sistema listo";
    }

    private async Task LoadStateAsync()
    {
        var profiles = await _repository.GetProfilesAsync();
        _profiles.Clear();
        foreach (var profile in profiles)
        {
            _profiles.Add(profile);
        }

        _activeProfile = profiles.FirstOrDefault(p => p.IsActive) ?? profiles.First();
        _buttons = await _repository.GetButtonsAsync(_activeProfile.Id);
        _voiceCommands = await _repository.GetVoiceCommandsAsync();

        ProfileNameTextBox.Text = _activeProfile.Name;
        ShipNameComboBox.Text = _activeProfile.ShipName;
        ProfileSelectComboBox.SelectedValue = _activeProfile.Id;
        ActiveProfileText.Text = _activeProfile.Name.ToUpperInvariant();
        ShipRoleText.Text = _activeProfile.ShipName.ToUpperInvariant();
        SelectComboText(VoiceModeComboBox, _settings.VoiceActivationMode);
        SelectComboText(PttDeviceComboBox, _settings.PushToTalkDevice);
        PttBindingTextBox.Text = _settings.PushToTalkBinding;
        VoiceConfidenceTextBox.Text = _settings.VoiceMinimumConfidence.ToString("0.00");
        CommandSoundCheckBox.IsChecked = _settings.CommandSoundEnabled;
        WelcomeSoundCheckBox.IsChecked = _settings.WelcomeSoundEnabled;

        RebuildDeckTiles();
        SelectButton(_buttons.FirstOrDefault(b => b.Name.Equals("Landing Gear", StringComparison.OrdinalIgnoreCase)) ?? _buttons.FirstOrDefault());

        PinText.Text = $"PIN de emparejamiento: {_settings.PairingPin}";
        await RefreshLogAsync();
        PlayWelcomeSound();
    }

    private void RebuildDeckTiles()
    {
        _buttonTiles.Clear();
        foreach (var button in _buttons.OrderBy(b => CategoryOrder(b.Category)).ThenBy(b => b.Name))
        {
            var phrases = _voiceCommands
                .Where(v => v.ButtonId == button.Id && v.Enabled)
                .Select(v => v.Phrase)
                .Take(2)
                .ToArray();
            _buttonTiles.Add(new DeckButtonTile(
                button.Id,
                button.Name,
                button.Category,
                DisplayAction(button.Action),
                IconPathFor(button.Icon, button.Name),
                ButtonFrameFor(button.AccentColor, button.Category, button.Name),
                string.Join(" / ", phrases),
                ToBrush(button.AccentColor),
                ToTileBrush(button.AccentColor)));
        }
    }

    private void SelectButton(DeckButton? button)
    {
        if (button is null)
        {
            return;
        }

        _selectedButton = button;
        SelectedButtonTitle.Text = button.Name;
        SelectedButtonMeta.Text = $"{button.Category}  /  {DisplayAction(button.Action)}";
        SelectedNameTextBox.Text = button.Name;
        SelectedCategoryComboBox.Text = button.Category;
        SelectedIconComboBox.Text = string.IsNullOrWhiteSpace(button.Icon) ? IconKeyForName(button.Name) : button.Icon;
        SelectedFrameComboBox.Text = FrameNameFor(button.AccentColor, button.Category, button.Name);
        SelectedKeyTextBox.Text = button.Action.Key;
        SelectedModifiersTextBox.Text = string.Join(", ", button.Action.Modifiers);
        SelectedConfirmCheckBox.IsChecked = button.RequiresConfirmation;
        VoicePhraseTextBox.Text = SuggestedPhrase(button);
        RefreshVoicePhraseList(button.Id);
    }

    private void RefreshVoicePhraseList(long buttonId)
    {
        _voicePhraseItems.Clear();
        foreach (var command in _voiceCommands.Where(v => v.ButtonId == buttonId).OrderBy(v => v.Phrase))
        {
            _voicePhraseItems.Add($"{command.Phrase}  [{command.MinimumConfidence:0.00}]");
        }
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSelectedButtonIfChangedAsync();

            var requestedName = ProfileNameTextBox.Text.Trim();
            var requestedShip = ShipNameComboBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                throw new InvalidOperationException("El nombre del perfil no puede estar vacio.");
            }

            if (string.IsNullOrWhiteSpace(requestedShip))
            {
                throw new InvalidOperationException("La nave del perfil no puede estar vacia.");
            }

            var sourceProfile = _activeProfile;
            _buttons = sourceProfile is null ? _buttons : await _repository.GetButtonsAsync(sourceProfile.Id);
            _voiceCommands = await _repository.GetVoiceCommandsAsync();
            var sourceButtons = _buttons.ToArray();
            var sourceVoiceCommands = _voiceCommands.ToArray();
            var profiles = await _repository.GetProfilesAsync();
            var existingProfile = profiles.FirstOrDefault(p => p.Name.Equals(requestedName, StringComparison.OrdinalIgnoreCase));
            var createsNewProfile = existingProfile is null;

            _activeProfile = await _repository.SaveProfileAsync(existingProfile is null
                ? new Profile(0, requestedName, requestedShip, "General", true)
                : existingProfile with { Name = requestedName, ShipName = requestedShip, Role = "General", IsActive = true });

            if (createsNewProfile)
            {
                await CloneDeckToProfileAsync(sourceButtons, sourceVoiceCommands, _activeProfile.Id);
            }
            else if (sourceProfile is not null && sourceProfile.Id != _activeProfile.Id)
            {
                var targetButtons = await _repository.GetButtonsAsync(_activeProfile.Id);
                if (targetButtons.Count == 0)
                {
                    await CloneDeckToProfileAsync(sourceButtons, sourceVoiceCommands, _activeProfile.Id);
                }
            }

            await _repository.AddCommandLogAsync("Windows", "Perfil", createsNewProfile ? $"Creado: {_activeProfile.Name}" : $"Guardado: {_activeProfile.Name}");
            WriteDebug($"Profile saved: {_activeProfile.Name} / {_activeProfile.ShipName} / {_activeProfile.Role} new={createsNewProfile}");
            await LoadStateAsync();
            StatusText.Text = createsNewProfile ? "Perfil nuevo creado" : "Perfil guardado";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task CloneDeckToProfileAsync(IReadOnlyList<DeckButton> sourceButtons, IReadOnlyList<VoiceCommand> sourceVoiceCommands, long targetProfileId)
    {
        var clonedButtonIds = new Dictionary<long, long>();
        foreach (var button in sourceButtons)
        {
            var clonedButton = await _repository.SaveButtonAsync(button with
            {
                Id = 0,
                ProfileId = targetProfileId
            });
            clonedButtonIds[button.Id] = clonedButton.Id;
        }

        foreach (var command in sourceVoiceCommands)
        {
            if (!clonedButtonIds.TryGetValue(command.ButtonId, out var clonedButtonId))
            {
                continue;
            }

            await _repository.SaveVoiceCommandAsync(command with
            {
                Id = 0,
                ButtonId = clonedButtonId
            });
        }

        WriteDebug($"Profile deck cloned: buttons={clonedButtonIds.Count} targetProfile={targetProfileId}");
    }

    private async void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSelectedButtonIfChangedAsync();

            var selectedProfile = ProfileSelectComboBox.SelectedItem as Profile
                ?? _profiles.FirstOrDefault(p => p.Id.Equals(ProfileSelectComboBox.SelectedValue));
            if (selectedProfile is null)
            {
                throw new InvalidOperationException("Selecciona un perfil guardado para cargarlo.");
            }

            _activeProfile = await _repository.SaveProfileAsync(selectedProfile with { IsActive = true });
            await _repository.AddCommandLogAsync("Windows", "Perfil", $"Cargado: {_activeProfile.Name}");
            WriteDebug($"Profile loaded: {_activeProfile.Name} / {_activeProfile.ShipName} / {_activeProfile.Role}");
            await LoadStateAsync();
            StatusText.Text = $"Perfil cargado: {_activeProfile.Name}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void SaveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var savedButton = await SaveSelectedButtonInternalAsync();
            await _repository.AddCommandLogAsync("Windows", savedButton.Name, $"Asignado a {DisplayAction(savedButton.Action)}");
            WriteDebug($"Button saved: {savedButton.Name} => {DisplayAction(savedButton.Action)} confirm={savedButton.RequiresConfirmation}");
            await ReloadDeckAsync();
            StatusText.Text = "Modulo guardado";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void CreateModule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureProfile();
            var moduleName = SelectedNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                throw new InvalidOperationException("Escribe un nombre para el modulo nuevo.");
            }

            var category = ComboTextValue(SelectedCategoryComboBox, "Custom");
            var icon = ComboTextValue(SelectedIconComboBox, "power");
            var frame = ComboTextValue(SelectedFrameComboBox, "Cyan");
            var keyText = string.IsNullOrWhiteSpace(SelectedKeyTextBox.Text) ? "F13" : SelectedKeyTextBox.Text.Trim();
            var action = new KeyPressAction(keyText, ParseModifiers(SelectedModifiersTextBox.Text), 60).Validate();

            var createdButton = await _repository.SaveButtonAsync(new DeckButton(
                0,
                _activeProfile!.Id,
                moduleName,
                icon,
                AccentForFrame(frame),
                category,
                action,
                SelectedConfirmCheckBox.IsChecked == true,
                true));

            await _repository.AddCommandLogAsync("Windows", createdButton.Name, $"Modulo creado {DisplayAction(createdButton.Action)}");
            WriteDebug($"Module created: {createdButton.Name} category={createdButton.Category} icon={createdButton.Icon} frame={frame} action={DisplayAction(createdButton.Action)}");
            _selectedButton = createdButton;
            await ReloadDeckAsync();
            SelectButton(_buttons.FirstOrDefault(b => b.Id == createdButton.Id) ?? createdButton);
            VoicePhraseTextBox.Text = createdButton.Name.ToLowerInvariant();
            StatusText.Text = $"Modulo creado: {createdButton.Name}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void DeleteModule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureSelectedButton();
            var result = MessageBox.Show(
                $"Eliminar el modulo '{_selectedButton!.Name}' y sus frases de voz?",
                "Eliminar modulo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var deletedName = _selectedButton.Name;
            await _repository.DeleteButtonAsync(_selectedButton.Id);
            await _repository.AddCommandLogAsync("Windows", deletedName, "Modulo eliminado");
            WriteDebug($"Module deleted: {deletedName} id={_selectedButton.Id}");
            _selectedButton = null;
            await ReloadDeckAsync();
            StatusText.Text = $"Modulo eliminado: {deletedName}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task SaveSelectedButtonIfChangedAsync()
    {
        if (_selectedButton is null || _activeProfile is null)
        {
            return;
        }

        var modifiers = ParseModifiers(SelectedModifiersTextBox.Text);
        var action = new KeyPressAction(SelectedKeyTextBox.Text.Trim(), modifiers, _selectedButton.Action.PressDurationMs).Validate();
        var requiresConfirmation = SelectedConfirmCheckBox.IsChecked == true;
        var name = SelectedNameTextBox.Text.Trim();
        var category = ComboTextValue(SelectedCategoryComboBox, _selectedButton.Category);
        var icon = ComboTextValue(SelectedIconComboBox, _selectedButton.Icon);
        var accent = AccentForFrame(ComboTextValue(SelectedFrameComboBox, FrameNameFor(_selectedButton.AccentColor, _selectedButton.Category, _selectedButton.Name)));
        var changed = !_selectedButton.Name.Equals(name, StringComparison.Ordinal)
            || !_selectedButton.Category.Equals(category, StringComparison.Ordinal)
            || !_selectedButton.Icon.Equals(icon, StringComparison.Ordinal)
            || !_selectedButton.AccentColor.Equals(accent, StringComparison.OrdinalIgnoreCase)
            || !_selectedButton.Action.Key.Equals(action.Key, StringComparison.OrdinalIgnoreCase)
            || !_selectedButton.Action.Modifiers.SequenceEqual(action.Modifiers, StringComparer.OrdinalIgnoreCase)
            || _selectedButton.RequiresConfirmation != requiresConfirmation;
        if (!changed)
        {
            return;
        }

        var savedButton = await SaveSelectedButtonInternalAsync();
        await _repository.AddCommandLogAsync("Windows", savedButton.Name, $"Auto guardado {DisplayAction(savedButton.Action)}");
        WriteDebug($"Button auto-saved before profile change: {savedButton.Name} => {DisplayAction(savedButton.Action)} confirm={savedButton.RequiresConfirmation}");
    }

    private async Task<DeckButton> SaveSelectedButtonInternalAsync()
    {
        EnsureProfile();
        EnsureSelectedButton();
        var action = new KeyPressAction(SelectedKeyTextBox.Text.Trim(), ParseModifiers(SelectedModifiersTextBox.Text), _selectedButton!.Action.PressDurationMs).Validate();
        var name = SelectedNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("El nombre del modulo no puede estar vacio.");
        }

        _selectedButton = await _repository.SaveButtonAsync(_selectedButton with
        {
            Name = name,
            Category = ComboTextValue(SelectedCategoryComboBox, _selectedButton.Category),
            Icon = ComboTextValue(SelectedIconComboBox, _selectedButton.Icon),
            AccentColor = AccentForFrame(ComboTextValue(SelectedFrameComboBox, FrameNameFor(_selectedButton.AccentColor, _selectedButton.Category, _selectedButton.Name))),
            Action = action,
            RequiresConfirmation = SelectedConfirmCheckBox.IsChecked == true
        });
        _buttons = await _repository.GetButtonsAsync(_activeProfile!.Id);
        return _selectedButton;
    }

    private static string[] ParseModifiers(string modifiersText)
    {
        return modifiersText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private async void DeckButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: long id })
        {
            return;
        }

        var button = _buttons.FirstOrDefault(b => b.Id == id);
        SelectButton(button);
        if (EditModeCheckBox.IsChecked == true)
        {
            StatusText.Text = button is null ? "Modo editar" : $"Editando {button.Name}";
            WriteDebug(button is null ? "Edit mode click ignored: no button" : $"Edit mode selected: {button.Name}");
            return;
        }

        await ExecuteButtonAsync(button, "Windows");
    }

    private async Task ExecuteButtonAsync(DeckButton? button, string source)
    {
        if (button is null)
        {
            return;
        }

        if (button.RequiresConfirmation && MessageBox.Show($"Ejecutar {button.Name}?", "Confirmar accion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _inputSender.SendAsync(button.Action);
            await _repository.AddCommandLogAsync(source, button.Name, $"Sent {DisplayAction(button.Action)}");
            WriteDebug($"{source} sent {button.Name} => {DisplayAction(button.Action)}");
            PlayCommandSound();
            await RefreshLogAsync();
            StatusText.Text = $"{button.Name} enviado";
        }
        catch (Exception ex)
        {
            WriteDebug($"{source} failed {button.Name}: {ex}");
            ShowError(ex);
        }
    }

    private async void ServerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _mobileServer ??= CreateMobileServer();
            await _mobileServer.StartAsync(_settings.MobilePort, _settings.PairingPin);
            var url = $"http://{_mobileServer.LocalIpAddress}:{_settings.MobilePort}";
            MobileUrlText.Text = url;
            QrImage.Source = CreateQr(url);
            ServerButton.Content = "Servidor activo";
            LanTopStatusText.Text = "ONLINE";
            LanTopStatusText.Foreground = ToBrush("#38E8FF");
            DevicesText.Text = $"Dispositivos conectados: {_mobileServer.ConnectedDevices.Count}";
            WriteDebug($"Mobile server started: {url}");
            StatusText.Text = "Panel movil LAN activo";
        }
        catch (Exception ex)
        {
            WriteDebug($"Mobile server failed: {ex}");
            ShowError(ex);
        }
    }

    private async void StopServer_Click(object sender, RoutedEventArgs e)
    {
        if (_mobileServer is not null)
        {
            await _mobileServer.StopAsync();
        }

        WriteDebug("Mobile server stopped");
        MobileUrlText.Text = "Servidor detenido";
        QrImage.Source = null;
        ServerButton.Content = "Activar servidor";
        LanTopStatusText.Text = "OFFLINE";
        LanTopStatusText.Foreground = ToBrush("#FFB545");
        DevicesText.Text = "Dispositivos conectados: 0";
    }

    private MobilePanelServer CreateMobileServer()
    {
        var server = new MobilePanelServer(_repository, _inputSender);
        server.Diagnostic += (_, message) => WriteDebug($"Mobile: {message}");
        return server;
    }

    private async void SaveVoice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureSelectedButton();
            var phrase = VoicePhraseTextBox.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(phrase))
            {
                throw new InvalidOperationException("La frase de voz no puede estar vacia.");
            }

            var confidence = double.TryParse(VoiceConfidenceTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
                ? Math.Clamp(parsed, 0.1, 0.98)
                : 0.74;
            var existing = _voiceCommands.FirstOrDefault(c => c.ButtonId == _selectedButton!.Id && c.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase));
            await _repository.SaveVoiceCommandAsync(new VoiceCommand(existing?.Id ?? 0, _selectedButton!.Id, phrase, confidence, true));
            await _repository.AddCommandLogAsync("Windows", "Voz", $"Frase guardada: {phrase}");
            WriteDebug($"Voice phrase saved: '{phrase}' => {_selectedButton.Name} confidence={confidence:0.00}");
            await ReloadDeckAsync();
            await RefreshVoiceRecognitionIfRunningAsync();
            StatusText.Text = "Frase de voz guardada";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureProfile();
            await SaveVoiceSettingsInternalAsync();
            if (_settings.VoiceActivationMode.Equals("PushToTalk", StringComparison.OrdinalIgnoreCase))
            {
                await StartVoiceRecognitionAsync(false);
                _voiceService.PauseRecognition();
                _pttMonitor.Start(_settings.PushToTalkDevice, _settings.PushToTalkBinding);
                VoiceStatusText.Text = $"PTT armado: manten {_settings.PushToTalkDevice}:{_settings.PushToTalkBinding}";
                VoiceTopStatusText.Text = "PTT";
                VoiceTopStatusText.Foreground = ToBrush("#FFB545");
                VoiceButton.Content = "PTT armado";
                WriteDebug($"Voice PTT armed with preloaded engine: {_settings.PushToTalkDevice}:{_settings.PushToTalkBinding}");
            }
            else
            {
                await StartVoiceRecognitionAsync(true);
                VoiceStatusText.Text = _voiceService.IsRunning ? "Escuchando comandos locales" : "No hay comandos de voz";
                VoiceTopStatusText.Text = _voiceService.IsRunning ? "ONLINE" : "OFFLINE";
                VoiceTopStatusText.Foreground = ToBrush(_voiceService.IsRunning ? "#38E8FF" : "#FFB545");
                VoiceButton.Content = "Voz activa";
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void StopVoice_Click(object sender, RoutedEventArgs e)
    {
        _pttMonitor.Stop();
        _pttPauseTimer.Stop();
        await _voiceService.StopAsync();
        WriteDebug("Voice service stopped");
        VoiceStatusText.Text = "Voz detenida";
        VoiceTopStatusText.Text = "OFFLINE";
        VoiceTopStatusText.Foreground = ToBrush("#FFB545");
        VoiceButton.Content = "Activar voz";
    }

    private async void SaveVoiceSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveVoiceSettingsInternalAsync();
            StatusText.Text = "Voz / PTT guardado";
            WriteDebug($"Voice settings saved: mode={_settings.VoiceActivationMode} device={_settings.PushToTalkDevice} binding={_settings.PushToTalkBinding}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void DetectPttButton_Click(object sender, RoutedEventArgs e)
    {
        _pttMonitor.Stop();
        _pttDetectWaitingForRelease = true;
        DetectPttButton.Content = "Suelta todo...";
        VoiceStatusText.Text = "Deteccion PTT: suelta teclado/raton/mando y pulsa el boton deseado";
        WriteDebug("PTT detection armed: waiting for all inputs released");
        _pttDetectTimer.Start();
    }

    private void PttDetectTimer_Tick(object? sender, EventArgs e)
    {
        if (_pttDetectWaitingForRelease)
        {
            if (PttInputMonitor.AnyInputPressed())
            {
                return;
            }

            _pttDetectWaitingForRelease = false;
            DetectPttButton.Content = "Pulsa ahora...";
            VoiceStatusText.Text = "Deteccion PTT: pulsa una tecla, raton, gamepad o joystick";
            WriteDebug("PTT detection ready: waiting for next input");
            return;
        }

        if (!PttInputMonitor.TryDetectPressed(out var detected))
        {
            return;
        }

        _pttDetectTimer.Stop();
        PttDeviceComboBox.Text = detected.DeviceType;
        SelectComboText(PttDeviceComboBox, detected.DeviceType);
        PttBindingTextBox.Text = detected.Binding;
        DetectPttButton.Content = "Detectar boton PTT";
        VoiceStatusText.Text = $"PTT detectado: {detected.DeviceType}:{detected.Binding}";
        WriteDebug($"PTT detected: {detected.DeviceType}:{detected.Binding}");
    }

    private async Task SaveVoiceSettingsInternalAsync()
    {
        var confidence = double.TryParse(VoiceConfidenceTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
            ? Math.Clamp(parsed, 0.1, 0.98)
            : _settings.VoiceMinimumConfidence;
        _settings = _settings with
        {
            VoiceActivationMode = ComboText(VoiceModeComboBox),
            PushToTalkDevice = ComboText(PttDeviceComboBox),
            PushToTalkBinding = ValidatePttBinding(ComboText(PttDeviceComboBox), PttBindingTextBox.Text),
            VoiceMinimumConfidence = confidence,
            CommandSoundEnabled = CommandSoundCheckBox.IsChecked == true,
            WelcomeSoundEnabled = WelcomeSoundCheckBox.IsChecked == true
        };
        await _repository.SaveSettingsAsync(_settings);
    }

    private void LoadWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.png");
        if (File.Exists(iconPath))
        {
            Icon = new BitmapImage(new Uri(iconPath));
        }
    }

    private void LoadAudioPlayers()
    {
        var welcomePath = Path.Combine(AppContext.BaseDirectory, "ready.mp3");
        var commandPath = Path.Combine(AppContext.BaseDirectory, "system.mp3");
        if (File.Exists(welcomePath))
        {
            _welcomePlayer.Open(new Uri(welcomePath));
            _welcomePlayer.Volume = 0.72;
        }

        if (File.Exists(commandPath))
        {
            _commandPlayer.Open(new Uri(commandPath));
            _commandPlayer.Volume = 0.62;
        }
    }

    private void PlayWelcomeSound()
    {
        if (_settings.WelcomeSoundEnabled)
        {
            PlayFromStart(_welcomePlayer);
        }
    }

    private void PlayCommandSound()
    {
        if (_settings.CommandSoundEnabled)
        {
            PlayFromStart(_commandPlayer);
        }
    }

    private static void PlayFromStart(MediaPlayer player)
    {
        try
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch
        {
            // Audio feedback is optional and must never interrupt command execution.
        }
    }

    private static string ValidatePttBinding(string deviceType, string bindingText)
    {
        var binding = bindingText.Trim();
        if (string.IsNullOrWhiteSpace(binding) || binding == "-")
        {
            throw new InvalidOperationException("Pulsa 'Detectar boton PTT' y asigna una tecla o boton valido.");
        }

        if (deviceType.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)
            || deviceType.Equals("Mouse", StringComparison.OrdinalIgnoreCase))
        {
            _ = KeyMap.ToVirtualKeyCode(binding);
        }

        return binding;
    }

    private async void PttMonitor_PressedChanged(object? sender, bool pressed)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            if (pressed)
            {
                _pttPauseTimer.Stop();
                _voiceService.SetInputGate(true);
                _voiceService.ResumeRecognition();
                VoiceStatusText.Text = "PTT pulsado: escuchando";
                VoiceTopStatusText.Text = "ONLINE";
                VoiceTopStatusText.Foreground = ToBrush("#38E8FF");
                WriteDebug("PTT pressed: voice input gate opened");
            }
            else
            {
                _voiceService.SetInputGate(false, TimeSpan.FromSeconds(2.5));
                _pttPauseTimer.Stop();
                _pttPauseTimer.Start();
                VoiceStatusText.Text = $"PTT armado: manten {_settings.PushToTalkDevice}:{_settings.PushToTalkBinding}";
                VoiceTopStatusText.Text = "PTT";
                VoiceTopStatusText.Foreground = ToBrush("#FFB545");
                WriteDebug("PTT released: voice input gate closing after 2.5s grace");
            }
        });
    }

    private void PttPauseTimer_Tick(object? sender, EventArgs e)
    {
        _pttPauseTimer.Stop();
        _voiceService.PauseRecognition();
        _voiceService.SetInputGate(false);
        WriteDebug("PTT grace elapsed: voice recognition paused");
    }

    private async Task StartVoiceRecognitionAsync(bool gateOpen)
    {
        _voiceCommands = await _repository.GetVoiceCommandsAsync();
        _buttons = await _repository.GetButtonsAsync(_activeProfile!.Id);
        var normalizedCommands = _voiceCommands
            .Select(c => c with { MinimumConfidence = Math.Min(c.MinimumConfidence, _settings.VoiceMinimumConfidence) })
            .ToList();
        if (!_voiceService.IsRunning)
        {
            await _voiceService.StartAsync(normalizedCommands, _buttons);
        }

        _voiceService.SetInputGate(gateOpen);
        WriteDebug($"Voice service start requested. Running={_voiceService.IsRunning}. Commands={normalizedCommands.Count}");
    }

    private async Task RefreshVoiceRecognitionIfRunningAsync()
    {
        if (!_voiceService.IsRunning)
        {
            return;
        }

        var pushToTalk = _settings.VoiceActivationMode.Equals("PushToTalk", StringComparison.OrdinalIgnoreCase);
        if (pushToTalk)
        {
            await StartVoiceRecognitionAsync(false);
            _voiceService.PauseRecognition();
            VoiceStatusText.Text = $"PTT actualizado: manten {_settings.PushToTalkDevice}:{_settings.PushToTalkBinding}";
            VoiceTopStatusText.Text = "PTT";
            VoiceTopStatusText.Foreground = ToBrush("#FFB545");
        }
        else
        {
            await StartVoiceRecognitionAsync(true);
            VoiceStatusText.Text = "Voz actualizada con nuevas frases";
            VoiceTopStatusText.Text = "ONLINE";
            VoiceTopStatusText.Foreground = ToBrush("#38E8FF");
        }

        WriteDebug("Voice service refreshed after voice phrase change");
    }

    private async void VoiceService_CommandRecognized(object? sender, VoiceRecognizedEventArgs e)
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            WriteDebug($"Voice recognized '{e.Command.Phrase}' confidence={e.Confidence:0.00} => {e.Button.Name}");
            SelectButton(e.Button);
            await ExecuteButtonAsync(e.Button, "Voice");
            VoiceStatusText.Text = $"Reconocido: {e.Command.Phrase} ({e.Confidence:0.00})";
        });
    }

    private void OpenDebugConsole_Click(object sender, RoutedEventArgs e)
    {
        WriteDebug("Debug console opened");
        var escapedPath = _logPath.Replace("'", "''");
        var command = $"Write-Host 'VerseDeck debug log: {escapedPath}'; Get-Content -LiteralPath '{escapedPath}' -Tail 80 -Wait";
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = true
        });
    }

    private async Task RefreshLiveStatusAsync()
    {
        await RefreshLogAsync();
        if (_mobileServer is not null)
        {
            DevicesText.Text = $"Dispositivos conectados: {_mobileServer.ConnectedDevices.Count}";
        }
    }

    private async Task ReloadDeckAsync()
    {
        EnsureProfile();
        _buttons = await _repository.GetButtonsAsync(_activeProfile!.Id);
        _voiceCommands = await _repository.GetVoiceCommandsAsync();
        var selectedId = _selectedButton?.Id;
        RebuildDeckTiles();
        SelectButton(_buttons.FirstOrDefault(b => b.Id == selectedId) ?? _buttons.FirstOrDefault());
        await RefreshLogAsync();
    }

    private async Task RefreshLogAsync()
    {
        _logItems.Clear();
        foreach (var item in await _repository.GetRecentCommandLogAsync(8))
        {
            _logItems.Add($"{item.CreatedAt:HH:mm:ss} [{item.Source}] {item.Command}: {item.Result}");
        }
    }

    private void WriteDebug(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Debug logging must never break input execution.
        }
    }

    private void EnsureProfile()
    {
        if (_activeProfile is null)
        {
            throw new InvalidOperationException("No active profile is loaded.");
        }
    }

    private void EnsureSelectedButton()
    {
        EnsureProfile();
        if (_selectedButton is null)
        {
            throw new InvalidOperationException("Selecciona un boton del MFD.");
        }
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Flight" => 0,
        "Navigation" => 1,
        "Scan" => 2,
        "Combat" => 3,
        "Utility" => 4,
        "Emergency" => 5,
        _ => 9
    };

    private static string SuggestedPhrase(DeckButton button) => button.Name switch
    {
        "Engines Toggle" => "encender motores",
        "Landing Gear" => "sacar tren de aterrizaje",
        "Lights" => "activar luces",
        "Radar Ping" => "activar radar",
        "Quantum Mode" => "activar salto",
        "Power Toggle" => "activar energia",
        _ => button.Name.ToLowerInvariant()
    };

    private static string DisplayAction(KeyPressAction action)
    {
        return action.Modifiers.Count == 0 ? action.Key : $"{string.Join("+", action.Modifiers)}+{action.Key}";
    }

    private static string IconPathFor(string icon, string name)
    {
        var iconKey = string.IsNullOrWhiteSpace(icon) ? IconKeyForName(name) : icon;
        return iconKey switch
        {
            "flight" => "Assets/Skin/icons/png/icon_flight_ready_256.png",
            "engine" => "Assets/Skin/icons/png/icon_engines_256.png",
            "gear" => "Assets/Skin/icons/png/icon_landing_gear_256.png",
            "power" => "Assets/Skin/icons/png/icon_power_256.png",
            "quantum" => "Assets/Skin/icons/png/icon_quantum_jump_256.png",
            "map" => "Assets/Skin/icons/png/icon_star_map_256.png",
            "radar" => "Assets/Skin/icons/png/icon_radar_256.png",
            "scan" => "Assets/Skin/icons/png/icon_scan_256.png",
            "shield" => "Assets/Skin/icons/png/icon_shields_256.png",
            "weapons" => "Assets/Skin/icons/png/icon_weapons_256.png",
            "cargo" => "Assets/Skin/icons/png/icon_cargo_256.png",
            "comms" => "Assets/Skin/icons/png/icon_comms_256.png",
            "doors" => "Assets/Skin/icons/png/icon_doors_256.png",
            "eject" => "Assets/Skin/icons/png/icon_eject_256.png",
            "warning" => "Assets/Skin/icons/png/icon_self_destruct_256.png",
            "lights" => "Assets/Skin/icons/png/icon_lights_256.png",
            _ => "Assets/Skin/icons/png/icon_power_256.png"
        };
    }

    private static string IconKeyForName(string name) => name switch
    {
        "Flight Ready" => "flight",
        "Engines Toggle" => "engine",
        "Landing Gear" => "gear",
        "Power Toggle" => "power",
        "Quantum Mode" => "quantum",
        "Star Map" => "map",
        "Radar Ping" => "radar",
        "Scan Mode" => "scan",
        "Shields" => "shield",
        "Weapons" => "weapons",
        "Cargo" => "cargo",
        "Comms" => "comms",
        "Doors" => "doors",
        "Eject" => "eject",
        "Self Destruct" => "warning",
        "Lights" => "lights",
        _ => "power"
    };

    private static string ButtonFrameFor(string accentColor, string category, string name)
    {
        var color = FrameNameFor(accentColor, category, name).ToLowerInvariant();
        return $"Assets/VideoUi/btn_{color}_normal.png";
    }

    private static string FrameNameFor(string accentColor, string category, string name)
    {
        if (IsNearColor(accentColor, "#FF4C58") || IsNearColor(accentColor, "#FF2E2E") || name is "Eject" or "Self Destruct")
        {
            return "Red";
        }

        if (IsNearColor(accentColor, "#31F6A5") || IsNearColor(accentColor, "#2EF6D1") || IsNearColor(accentColor, "#54D6A7") || IsNearColor(accentColor, "#A7C957"))
        {
            return "Green";
        }

        return category switch
        {
            "Scan" or "Utility" => "Green",
            "Combat" or "Emergency" => "Red",
            _ => "Cyan"
        };
    }

    private static string AccentForFrame(string frame) => frame.Trim().ToLowerInvariant() switch
    {
        "green" => "#31F6A5",
        "red" => "#FF4C58",
        _ => "#49E7FF"
    };

    private static bool IsNearColor(string actual, string expected)
    {
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComboTextValue(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        var text = ComboText(comboBox).Trim();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static Brush ToBrush(string color)
    {
        return (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private static Brush ToTileBrush(string color)
    {
        var accent = (Color)ColorConverter.ConvertFromString(color);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(96, accent.R, accent.G, accent.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(3, 13, 18), 0.72));
        brush.Freeze();
        return brush;
    }

    private static BitmapImage CreateQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string ComboText(System.Windows.Controls.ComboBox comboBox)
    {
        return comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
            ? item.Content.ToString() ?? string.Empty
            : comboBox.Text;
    }

    private static void SelectComboText(System.Windows.Controls.ComboBox comboBox, string value)
    {
        for (var i = 0; i < comboBox.Items.Count; i++)
        {
            if (((System.Windows.Controls.ComboBoxItem)comboBox.Items[i]).Content.ToString() == value)
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        comboBox.Text = value;
    }

    private static void ShowError(Exception ex)
    {
        MessageBox.Show(ex.Message, "VerseDeck Companion", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        await _voiceService.StopAsync();
        _pttMonitor.Stop();
        _pttDetectTimer.Stop();
        _pttPauseTimer.Stop();
        _refreshTimer.Stop();
        WriteDebug("App closing");
        if (_mobileServer is not null)
        {
            await _mobileServer.StopAsync();
        }
    }

    private sealed record DeckButtonTile(
        long Id,
        string Name,
        string Category,
        string KeyText,
        string IconImage,
        string ButtonFrameImage,
        string VoiceSummary,
        Brush AccentBrush,
        Brush TileBrush);
}

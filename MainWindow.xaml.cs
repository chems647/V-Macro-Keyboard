using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacroFenetre.Models;
using MacroFenetre.Services;
using Microsoft.Win32;

namespace MacroFenetre;

public partial class MainWindow : Window
{
    private const int EscapeVirtualKey = 0x1B;

    private readonly ObservableCollection<WindowItem> _windows = [];
    private readonly ObservableCollection<WindowGroup> _windowGroups = [];
    private readonly ObservableCollection<WindowItem> _selectedWindows = [];
    private readonly ObservableCollection<ClickMacro> _macros = [];
    private readonly ObservableCollection<ActionSequenceMacro> _actionSequences = [];
    private readonly Dictionary<(int VirtualKey, HotkeyModifiers Modifiers), ClickMacro> _clickMacrosByGesture = [];
    private readonly Dictionary<(int VirtualKey, HotkeyModifiers Modifiers), ActionSequenceMacro> _sequencesByGesture = [];
    private readonly Dictionary<int, ActionSequenceMacro> _sequencesByMouseButton = [];
    private readonly List<WindowConfiguration> _rememberedWindowSelections = [];
    private readonly HashSet<int> _consumedKeys = [];
    private readonly HashSet<int> _consumedMouseButtons = [];
    private readonly List<ClickMarkerWindow> _captureMarkers = [];
    private readonly SemaphoreSlim _windowActionLock = new(1, 1);
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private CancellationTokenSource? _autoSaveCancellation;
    private KeyboardHook? _keyboardHook;
    private GlobalMouseHook? _globalMouseHook;
    private MouseCaptureHook? _mouseCaptureHook;
    private WindowItem? _pendingTarget;
    private KeyChoice? _pendingKey;
    private KeyChoice? _pendingModifierInput;
    private ClickMacro? _editingMacro;
    private ActionSequenceMacro? _editingActionSequence;
    private KeyChoice? _macroKeyBeforeEdit;
    private InputTrigger? _sequenceTriggerBeforeEdit;
    private string? _sequenceActionsBeforeEdit;
    private nint? _targetHandleBeforeEdit;
    private TextBox? _activeKeyInput;
    private KeyChoice _switchKey = KeyChoice.F8;
    private KeyChoice _macroKey = KeyChoice.F6;
    private InputTrigger _sequenceTrigger = InputTrigger.Keyboard(new KeyChoice("F7", 0x76));
    private bool _pendingApplyToMatchingWindows;
    private bool _applyToMatchingWindowsBeforeEdit;
    private bool _isLoaded;
    private bool _isRefreshingWindows;
    private bool _isCapturing;
    private bool _isExecutingMacro;
    private bool _isExecutingActionSequence;
    private bool _isKeyInputFocused;
    private bool _keyInputCommitted;
    private bool _shortcutsEnabled = true;
    private bool _isLoadingConfiguration;

    public MainWindow()
    {
        InitializeComponent();
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _macros.CollectionChanged += (_, _) => RebuildShortcutIndexes();
        _actionSequences.CollectionChanged += (_, _) => RebuildShortcutIndexes();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        WindowsList.ItemsSource = _windowGroups;
        MacroTargetCombo.ItemsSource = _selectedWindows;
        MacrosGrid.ItemsSource = _macros;
        ActionSequencesGrid.ItemsSource = _actionSequences;
        SwitchKeyTextBox.Text = _switchKey.Name;
        MacroKeyTextBox.Text = _macroKey.Name;
        SequenceTriggerTextBox.Text = _sequenceTrigger.Name;

        _isLoaded = true;
        RefreshWindows();

        var restoredConfiguration = false;
        var autoSavePath = ConfigurationService.FindAutoSavePathToLoad();
        if (autoSavePath is not null)
        {
            try
            {
                var configuration = await ConfigurationService.LoadAsync(autoSavePath);
                ApplyConfiguration(configuration);
                if (!autoSavePath.Equals(ConfigurationService.AutoSavePath, StringComparison.OrdinalIgnoreCase))
                {
                    await ConfigurationService.SaveAsync(
                        ConfigurationService.AutoSavePath,
                        BuildConfiguration());
                }

                restoredConfiguration = true;
                SetStatus(
                    $"Sauvegarde restaurée — {_macros.Count} clic(s), {_actionSequences.Count} séquence(s).",
                    StatusKind.Ready);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                SetStatus($"La sauvegarde automatique n’a pas pu être chargée : {exception.Message}", StatusKind.Warning);
            }
        }

        try
        {
            _keyboardHook = new KeyboardHook(HandleGlobalKey);
            if (!restoredConfiguration)
            {
                SetStatus("Prêt — F8 bascule entre les fenêtres sélectionnées.", StatusKind.Ready);
            }
        }
        catch (Win32Exception exception)
        {
            _shortcutsEnabled = false;
            MasterToggle.IsChecked = false;
            SetStatus(exception.Message, StatusKind.Error);
            MessageBox.Show(this, exception.Message, "Raccourcis indisponibles", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        try
        {
            _globalMouseHook = new GlobalMouseHook(HandleGlobalMouseButton);
        }
        catch (Win32Exception exception)
        {
            SetStatus(exception.Message, StatusKind.Warning);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _autoSaveTimer.Stop();
        _autoSaveCancellation?.Cancel();
        try
        {
            ConfigurationService.Save(ConfigurationService.AutoSavePath, BuildConfiguration());
        }
        catch (Exception)
        {
            // Closing must remain possible even if the profile directory is unavailable.
        }

        CloseCaptureMarkers();
        DisposeWindowGroups();
        _mouseCaptureHook?.Dispose();
        _globalMouseHook?.Dispose();
        _keyboardHook?.Dispose();
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var selectedHandles = _windows
            .Where(window => window.IsSelected)
            .Select(window => window.Handle)
            .ToHashSet();
        var expansionStates = CaptureExpansionStates();
        var ownHandle = new WindowInteropHelper(this).Handle;
        var currentWindows = WindowService.EnumerateVisibleWindows(ownHandle);

        _isRefreshingWindows = true;
        try
        {
            DisposeWindowGroups();
            _windows.Clear();
            foreach (var window in currentWindows)
            {
                window.IsSelected = selectedHandles.Contains(window.Handle) ||
                                    _rememberedWindowSelections.Any(saved =>
                                        saved.ProcessName.Equals(window.ProcessName, StringComparison.CurrentCultureIgnoreCase) &&
                                        TitlesLikelyMatch(saved.Title, window.Title));
                _windows.Add(window);
            }

            RebuildWindowGroups(expansionStates);
        }
        finally
        {
            _isRefreshingWindows = false;
        }

        RebuildSelectedWindows();
        SetStatus(
            $"{_windows.Count} fenêtre{(_windows.Count > 1 ? "s" : string.Empty)} " +
            $"détectée{(_windows.Count > 1 ? "s" : string.Empty)} dans {_windowGroups.Count} groupe{(_windowGroups.Count > 1 ? "s" : string.Empty)}.",
            StatusKind.Ready);
    }

    private void RebuildWindowGroups(IReadOnlyDictionary<string, bool>? expansionStates = null)
    {
        expansionStates ??= CaptureExpansionStates();
        DisposeWindowGroups();

        var query = WindowSearchBox.Text.Trim();
        var matchingGroups = _windows
            .GroupBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => query.Length == 0 || group.Any(window =>
                window.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase);

        foreach (var group in matchingGroups)
        {
            var windows = group
                .OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var isExpanded = expansionStates.TryGetValue(group.Key, out var previousState)
                ? previousState
                : windows.Length == 1;
            _windowGroups.Add(new WindowGroup(group.Key, windows, isExpanded));
        }
    }

    private Dictionary<string, bool> CaptureExpansionStates() =>
        _windowGroups.ToDictionary(
            group => group.ProcessName,
            group => group.IsExpanded,
            StringComparer.CurrentCultureIgnoreCase);

    private void DisposeWindowGroups()
    {
        foreach (var group in _windowGroups)
        {
            group.Dispose();
        }

        _windowGroups.Clear();
    }

    private void WindowSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoaded)
        {
            RebuildWindowGroups();
        }
    }

    private void GroupSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: WindowGroup group })
        {
            return;
        }

        _isRefreshingWindows = true;
        try
        {
            group.SetAll(group.SelectionState != true);
        }
        finally
        {
            _isRefreshingWindows = false;
        }

        RebuildSelectedWindows();
        ScheduleAutoSave();
        e.Handled = true;
    }

    private void WindowSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingWindows)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            RebuildSelectedWindows();
            ScheduleAutoSave();
        }, DispatcherPriority.DataBind);
    }

    private void RebuildSelectedWindows()
    {
        var previousTargetHandle = (MacroTargetCombo.SelectedItem as WindowItem)?.Handle;
        var selected = _windows.Where(window => window.IsSelected).ToArray();

        _selectedWindows.Clear();
        foreach (var window in selected)
        {
            _selectedWindows.Add(window);
        }

        MacroTargetCombo.SelectedItem = _selectedWindows.FirstOrDefault(window => window.Handle == previousTargetHandle)
                                        ?? _selectedWindows.FirstOrDefault();
        CaptureButton.IsEnabled = _selectedWindows.Count > 0 && !_isCapturing;

        SelectedWindowCount.Text = _selectedWindows.Count switch
        {
            0 => "0 fenêtre sélectionnée",
            1 => "1 fenêtre sélectionnée",
            _ => $"{_selectedWindows.Count} fenêtres sélectionnées"
        };
    }

    private void MasterToggle_Changed(object sender, RoutedEventArgs e)
    {
        _shortcutsEnabled = MasterToggle.IsChecked == true;
        if (!_isLoaded)
        {
            return;
        }

        SetStatus(
            _shortcutsEnabled ? "Raccourcis globaux activés." : "Raccourcis globaux en pause.",
            _shortcutsEnabled ? StatusKind.Ready : StatusKind.Warning);
        ScheduleAutoSave();
    }

    private void KeyInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        _activeKeyInput = textBox;
        _pendingModifierInput = null;
        _keyInputCommitted = false;
        _isKeyInputFocused = true;
        textBox.Text = "Appuyez sur une touche…";
        textBox.SelectAll();
    }

    private void KeyInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox && !_keyInputCommitted)
        {
            textBox.Text = textBox == SwitchKeyTextBox
                ? _switchKey.Name
                : textBox == MacroKeyTextBox
                    ? _macroKey.Name
                    : _sequenceTrigger.Name;
        }

        _activeKeyInput = null;
        _pendingModifierInput = null;
        _isKeyInputFocused = false;
    }

    private void KeyInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        var key = ResolveKey(e);
        e.Handled = true;

        if (IsModifierKey(key))
        {
            _pendingModifierInput = new KeyChoice(
                GetKeyName(key),
                KeyInterop.VirtualKeyFromKey(key));
            _keyInputCommitted = false;
            textBox.Text = $"{FormatModifiers(ToHotkeyModifiers(Keyboard.Modifiers))}…";
            SetStatus("Relâchez cette touche pour l’utiliser seule, ou ajoutez une autre touche pour créer une combinaison.", StatusKind.Capture);
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            SetStatus("Cette touche n’a pas pu être reconnue. Essayez-en une autre.", StatusKind.Error);
            return;
        }

        var choice = new KeyChoice(
            FormatKeyChoice(key, ToHotkeyModifiers(Keyboard.Modifiers)),
            virtualKey,
            ToHotkeyModifiers(Keyboard.Modifiers));
        _pendingModifierInput = null;
        ApplyCapturedKey(textBox, choice);
    }

    private void KeyInput_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || _pendingModifierInput is null || _keyInputCommitted)
        {
            return;
        }

        var releasedVirtualKey = KeyInterop.VirtualKeyFromKey(ResolveKey(e));
        if (releasedVirtualKey != _pendingModifierInput.VirtualKey)
        {
            return;
        }

        e.Handled = true;
        var choice = _pendingModifierInput;
        _pendingModifierInput = null;
        ApplyCapturedKey(textBox, choice);
    }

    private void ApplyCapturedKey(TextBox textBox, KeyChoice choice)
    {
        if (textBox == SwitchKeyTextBox)
        {
            if (_macros.Any(macro => GestureEquals(macro, choice)) ||
                _actionSequences.Any(sequence => sequence.Trigger.MatchesKeyboard(choice.VirtualKey, choice.Modifiers)))
            {
                textBox.Text = _switchKey.Name;
                SetStatus($"{choice.Name} est déjà utilisée par une macro.", StatusKind.Error);
                return;
            }

            _switchKey = choice;
            SetStatus($"{choice.Name} basculera uniquement entre les fenêtres cochées.", StatusKind.Ready);
            ScheduleAutoSave();
        }
        else if (textBox == MacroKeyTextBox)
        {
            if (_switchKey.Matches(choice.VirtualKey, choice.Modifiers))
            {
                textBox.Text = _macroKey.Name;
                SetStatus($"{choice.Name} sert déjà à changer de fenêtre.", StatusKind.Error);
                return;
            }

            _macroKey = choice;
            var hasConflict = _macros.Any(macro => macro != _editingMacro && GestureEquals(macro, choice)) ||
                              _actionSequences.Any(sequence =>
                                  sequence.Trigger.MatchesKeyboard(choice.VirtualKey, choice.Modifiers));
            SetStatus(
                !hasConflict
                    ? $"Touche choisie : {choice.Name}."
                    : $"{choice.Name} est déjà associée à un clic ; choisissez une autre touche.",
                !hasConflict ? StatusKind.Ready : StatusKind.Warning);
        }
        else
        {
            var trigger = InputTrigger.Keyboard(choice);
            if (TriggerConflicts(trigger, _editingActionSequence))
            {
                textBox.Text = _sequenceTrigger.Name;
                SetStatus($"{choice.Name} est déjà utilisé comme raccourci.", StatusKind.Error);
                return;
            }

            _sequenceTrigger = trigger;
            SetStatus($"Déclencheur choisi : {choice.Name}.", StatusKind.Ready);
        }

        textBox.Text = choice.Name;
        textBox.SelectAll();
        _keyInputCommitted = true;
    }

    private bool HandleGlobalKey(int virtualKey, bool isDown)
    {
        var isModifier = TryGetModifier(virtualKey, out _);
        if (!isDown)
        {
            var wasConsumed = _consumedKeys.Remove(virtualKey);
            return wasConsumed;
        }

        if (_isKeyInputFocused)
        {
            return false;
        }

        if (_isCapturing && virtualKey == EscapeVirtualKey)
        {
            if (_consumedKeys.Add(virtualKey))
            {
                Dispatcher.BeginInvoke(() => CancelCapture("Capture annulée.", StatusKind.Warning));
            }

            return true;
        }

        if (_isCapturing || !_shortcutsEnabled)
        {
            return false;
        }

        var modifiers = isModifier ? HotkeyModifiers.None : GetPressedModifiers();
        _clickMacrosByGesture.TryGetValue((virtualKey, modifiers), out var macro);
        _sequencesByGesture.TryGetValue((virtualKey, modifiers), out var actionSequence);
        var isSwitchKey = _switchKey.Matches(virtualKey, modifiers);
        if (!isSwitchKey && macro is null && actionSequence is null)
        {
            return false;
        }

        if (!_consumedKeys.Add(virtualKey))
        {
            return true;
        }

        if (isSwitchKey)
        {
            Dispatcher.BeginInvoke(SwitchToNextSelectedWindow);
        }
        else if (macro is not null)
        {
            Dispatcher.BeginInvoke(() => ExecuteMacroAsync(macro));
        }
        else if (actionSequence is not null)
        {
            Dispatcher.BeginInvoke(() => ExecuteActionSequenceAsync(actionSequence));
        }

        return true;
    }

    private bool HandleGlobalMouseButton(int buttonCode, bool isDown)
    {
        if (!isDown)
        {
            return _consumedMouseButtons.Remove(buttonCode);
        }

        if (_activeKeyInput == SequenceTriggerTextBox)
        {
            if (_consumedMouseButtons.Add(buttonCode))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var trigger = new InputTrigger(
                        GlobalMouseHook.GetButtonName(buttonCode),
                        InputTriggerKind.Mouse,
                        buttonCode);
                    if (TriggerConflicts(trigger, _editingActionSequence))
                    {
                        SequenceTriggerTextBox.Text = _sequenceTrigger.Name;
                        SetStatus($"{trigger.Name} est déjà utilisé comme raccourci.", StatusKind.Error);
                        return;
                    }

                    _sequenceTrigger = trigger;
                    SequenceTriggerTextBox.Text = trigger.Name;
                    SequenceTriggerTextBox.SelectAll();
                    _keyInputCommitted = true;
                    SetStatus($"Déclencheur choisi : {trigger.Name}.", StatusKind.Ready);
                });
            }

            return true;
        }

        if (_isKeyInputFocused || _isCapturing || !_shortcutsEnabled)
        {
            return false;
        }

        if (!_sequencesByMouseButton.TryGetValue(buttonCode, out var actionSequence))
        {
            return false;
        }

        if (_consumedMouseButtons.Add(buttonCode))
        {
            Dispatcher.BeginInvoke(() => ExecuteActionSequenceAsync(actionSequence));
        }

        return true;
    }

    private async void SwitchToNextSelectedWindow()
    {
        var validWindows = _selectedWindows
            .Where(window => NativeMethods.IsWindow(window.Handle))
            .ToArray();

        if (validWindows.Length == 0)
        {
            SetStatus("Aucune fenêtre sélectionnée n’est encore disponible.", StatusKind.Error);
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var currentIndex = Array.FindIndex(validWindows, window => window.Handle == foreground);
        var nextWindow = validWindows[(currentIndex + 1) % validWindows.Length];

        await _windowActionLock.WaitAsync();
        try
        {
            if (await ActivateAndWaitAsync(nextWindow.Handle, Math.Min(StabilizationDelayMs, 350)))
            {
                SetStatus($"Fenêtre active : {nextWindow.Title}", StatusKind.Ready);
            }
            else
            {
                SetStatus("La fenêtre choisie ne peut plus être activée. Actualisez la liste.", StatusKind.Error);
            }
        }
        finally
        {
            _windowActionLock.Release();
        }
    }

    private async Task<bool> ActivateAndWaitAsync(nint handle, int settleDelayMs)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            WindowService.ActivateWindow(handle);

            for (var poll = 0; poll < 12; poll++)
            {
                if (NativeMethods.GetForegroundWindow() == handle &&
                    WindowService.TryGetClientArea(handle, out _, out _, out _))
                {
                    await Task.Delay(settleDelayMs);
                    return NativeMethods.GetForegroundWindow() == handle;
                }

                await Task.Delay(35);
            }

            await Task.Delay(80);
        }

        return false;
    }

    private async void CapturePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_isCapturing || MacroTargetCombo.SelectedItem is not WindowItem target)
        {
            return;
        }

        if (_switchKey.Matches(_macroKey.VirtualKey, _macroKey.Modifiers))
        {
            SetStatus($"{_macroKey.Name} sert déjà à changer de fenêtre.", StatusKind.Error);
            return;
        }

        var conflictingMacro = _macros.FirstOrDefault(macro =>
            macro != _editingMacro && GestureEquals(macro, _macroKey));
        var conflictingSequence = _actionSequences.FirstOrDefault(sequence =>
            sequence.Trigger.MatchesKeyboard(_macroKey.VirtualKey, _macroKey.Modifiers));
        if (conflictingMacro is not null || conflictingSequence is not null)
        {
            MessageBox.Show(
                this,
                $"{_macroKey.Name} est déjà associée à un autre raccourci. Modifiez-le ou choisissez une autre touche.",
                "Touche déjà utilisée",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _pendingTarget = target;
        _pendingKey = _macroKey;
        _pendingApplyToMatchingWindows = ApplyAllMatchingToggle.IsChecked == true;
        _isCapturing = true;
        CaptureButton.IsEnabled = false;
        SetStatus($"Capture active : cliquez dans « {target.Title} » — Échap pour annuler.", StatusKind.Capture);

        try
        {
            _mouseCaptureHook = new MouseCaptureHook(point => Dispatcher.BeginInvoke(() => CompleteCaptureAsync(point)));
        }
        catch (Win32Exception exception)
        {
            CancelCapture(exception.Message, StatusKind.Error);
            return;
        }

        WindowState = WindowState.Minimized;
        await Task.Delay(120);

        if (!_isCapturing)
        {
            return;
        }

        if (!await ActivateAndWaitAsync(target.Handle, 100))
        {
            CancelCapture("La fenêtre cible n’est plus disponible.", StatusKind.Error);
            return;
        }

        await Task.Delay(90);
        if (_isCapturing)
        {
            ShowExistingMacroMarkers(target);
        }
    }

    private async void CompleteCaptureAsync(NativeMethods.Point point)
    {
        _mouseCaptureHook?.Dispose();
        _mouseCaptureHook = null;
        CloseCaptureMarkers();

        var target = _pendingTarget;
        var key = _pendingKey;
        var applyToMatchingWindows = _pendingApplyToMatchingWindows;
        _isCapturing = false;

        if (target is null || key is null ||
            !WindowService.IsPointInClientArea(target.Handle, point) ||
            !WindowService.TryGetClientArea(target.Handle, out var origin, out var width, out var height))
        {
            ClearPendingCapture();
            RestoreMainWindow();
            RebuildSelectedWindows();
            SetStatus("Le point doit se trouver dans la fenêtre cible. Recommencez la capture.", StatusKind.Error);
            return;
        }

        var relativeX = Math.Clamp((double)(point.X - origin.X) / width, 0, 1);
        var relativeY = Math.Clamp((double)(point.Y - origin.Y) / height, 0, 1);
        var clientX = point.X - origin.X;
        var clientY = point.Y - origin.Y;
        var editedIndex = _editingMacro is null ? -1 : _macros.IndexOf(_editingMacro);
        if (editedIndex >= 0)
        {
            _macros.RemoveAt(editedIndex);
        }

        var newMacro = new ClickMacro
        {
            VirtualKey = key.VirtualKey,
            Modifiers = key.Modifiers,
            KeyName = key.Name,
            WindowHandle = target.Handle,
            WindowTitle = target.Title,
            ProcessName = target.ProcessName,
            RelativeX = relativeX,
            RelativeY = relativeY,
            ClientX = clientX,
            ClientY = clientY,
            ReferenceWidth = width,
            ReferenceHeight = height,
            ApplyToMatchingWindows = applyToMatchingWindows
        };

        if (editedIndex >= 0)
        {
            _macros.Insert(editedIndex, newMacro);
        }
        else
        {
            _macros.Add(newMacro);
        }

        ResetEditState();
        ClearPendingCapture();
        ScheduleAutoSave();

        var marker = new ClickMarkerWindow(point.X, point.Y, key.Name, false);
        marker.ShowBriefly();
        SetStatus(
            applyToMatchingWindows
                ? $"{key.Name} appliquera ce clic à toutes les fenêtres {target.ProcessName} sélectionnées."
                : $"{key.Name} est maintenant associée à ce point.",
            StatusKind.Ready);

        await Task.Delay(700);
        RestoreMainWindow();
        RebuildSelectedWindows();
    }

    private void ShowExistingMacroMarkers(WindowItem target)
    {
        CloseCaptureMarkers();
        if (!WindowService.TryGetClientArea(target.Handle, out var origin, out var width, out var height))
        {
            return;
        }

        foreach (var macro in _macros.Where(macro => macro != _editingMacro && MacroAppliesToTarget(macro, target)))
        {
            var (clientX, clientY) = GetClickClientPosition(macro, width, height);
            var markerX = origin.X + clientX;
            var markerY = origin.Y + clientY;
            var marker = new ClickMarkerWindow(markerX, markerY, macro.KeyName, true);
            marker.ShowPersistent();
            _captureMarkers.Add(marker);
        }
    }

    private static bool MacroAppliesToTarget(ClickMacro macro, WindowItem target)
    {
        if (macro.ApplyToMatchingWindows)
        {
            return macro.ProcessName.Equals(target.ProcessName, StringComparison.CurrentCultureIgnoreCase) &&
                   target.IsSelected;
        }

        return macro.WindowHandle == target.Handle ||
               (macro.ProcessName.Equals(target.ProcessName, StringComparison.CurrentCultureIgnoreCase) &&
                macro.WindowTitle.Equals(target.Title, StringComparison.CurrentCultureIgnoreCase));
    }

    private static (int X, int Y) GetClickClientPosition(ClickMacro macro, int width, int height)
    {
        if (macro.ReferenceWidth > 0 && macro.ReferenceHeight > 0 &&
            macro.ClientX >= 0 && macro.ClientX < width &&
            macro.ClientY >= 0 && macro.ClientY < height)
        {
            return (macro.ClientX, macro.ClientY);
        }

        return (
            (int)Math.Round(macro.RelativeX * Math.Max(0, width - 1)),
            (int)Math.Round(macro.RelativeY * Math.Max(0, height - 1)));
    }

    private void CloseCaptureMarkers()
    {
        foreach (var marker in _captureMarkers)
        {
            marker.Close();
        }

        _captureMarkers.Clear();
    }

    private void CancelCapture(string message, StatusKind statusKind)
    {
        _mouseCaptureHook?.Dispose();
        _mouseCaptureHook = null;
        CloseCaptureMarkers();
        ClearPendingCapture();
        _isCapturing = false;
        RestoreMainWindow();
        RebuildSelectedWindows();
        SetStatus(message, statusKind);
    }

    private void ClearPendingCapture()
    {
        _pendingTarget = null;
        _pendingKey = null;
    }

    private void RestoreMainWindow()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    private async void ExecuteMacroAsync(ClickMacro macro)
    {
        if (_isExecutingMacro)
        {
            return;
        }

        _isExecutingMacro = true;
        var targets = ResolveMacroTargets(macro);
        if (targets.Count == 0)
        {
            SetStatus("Aucune fenêtre cible compatible n’est disponible.", StatusKind.Error);
            _isExecutingMacro = false;
            return;
        }

        NativeMethods.GetCursorPos(out var originalCursorPosition);
        var clickedCount = 0;
        await _windowActionLock.WaitAsync();

        try
        {
            if (!await WaitForShortcutReleaseAsync(
                    new InputTrigger(macro.KeyName, InputTriggerKind.Keyboard, macro.VirtualKey, macro.Modifiers)))
            {
                SetStatus(
                    $"Relâchez complètement {macro.KeyName} avant de relancer cette macro.",
                    StatusKind.Warning);
                return;
            }

            foreach (var targetHandle in targets)
            {
                if (!await ActivateAndWaitAsync(targetHandle, StabilizationDelayMs))
                {
                    continue;
                }

                if (!WindowService.TryGetClientArea(targetHandle, out var origin, out var width, out var height))
                {
                    continue;
                }

                var (clientX, clientY) = GetClickClientPosition(macro, width, height);
                var clickX = origin.X + clientX;
                var clickY = origin.Y + clientY;
                if (!NativeMethods.SetCursorPos(clickX, clickY))
                {
                    continue;
                }

                await Task.Delay(55);
                if (NativeMethods.GetForegroundWindow() != targetHandle)
                {
                    continue;
                }

                if (!NativeMethods.GetCursorPos(out var confirmedCursorPosition) ||
                    Math.Abs(confirmedCursorPosition.X - clickX) > 1 ||
                    Math.Abs(confirmedCursorPosition.Y - clickY) > 1)
                {
                    NativeMethods.SetCursorPos(clickX, clickY);
                    await Task.Delay(35);
                }

                if (!SendLeftClick())
                {
                    continue;
                }

                clickedCount++;
                await Task.Delay(Math.Max(120, StabilizationDelayMs / 2));
            }
        }
        finally
        {
            if (RestoreCursorToggle.IsChecked == true)
            {
                NativeMethods.SetCursorPos(originalCursorPosition.X, originalCursorPosition.Y);
            }

            _isExecutingMacro = false;
            _windowActionLock.Release();
        }

        SetStatus(
            clickedCount switch
            {
                0 => "Le clic n’a pu être exécuté sur aucune fenêtre.",
                1 => $"Macro {macro.KeyName} exécutée.",
                _ => $"Macro {macro.KeyName} exécutée sur {clickedCount} fenêtres."
            },
            clickedCount == 0 ? StatusKind.Error : StatusKind.Ready);
    }

    private static bool AreModifierKeysPhysicallyDown() =>
        IsKeyDown(0x10) || IsKeyDown(0x11) || IsKeyDown(0x12) ||
        IsKeyDown(0x5B) || IsKeyDown(0x5C) ||
        IsKeyDown(0xA0) || IsKeyDown(0xA1) ||
        IsKeyDown(0xA2) || IsKeyDown(0xA3) ||
        IsKeyDown(0xA4) || IsKeyDown(0xA5);

    private static async Task<bool> WaitForShortcutReleaseAsync(InputTrigger trigger)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (!IsTriggerPhysicallyDown(trigger) && !AreModifierKeysPhysicallyDown())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return !IsTriggerPhysicallyDown(trigger) && !AreModifierKeysPhysicallyDown();
    }

    private static bool IsTriggerPhysicallyDown(InputTrigger trigger)
    {
        if (trigger.Kind == InputTriggerKind.Keyboard)
        {
            return IsKeyDown(trigger.Code);
        }

        var mouseVirtualKey = trigger.Code switch
        {
            GlobalMouseHook.MiddleButton => 0x04,
            GlobalMouseHook.SideButton1 => 0x05,
            GlobalMouseHook.SideButton2 => 0x06,
            _ => 0
        };
        return mouseVirtualKey != 0 && IsKeyDown(mouseVirtualKey);
    }

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool SendLeftClick()
    {
        var inputs = new[]
        {
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput { Flags = NativeMethods.MouseEventLeftDown }
                }
            },
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput { Flags = NativeMethods.MouseEventLeftUp }
                }
            }
        };

        return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()) == (uint)inputs.Length;
    }

    private async void ExecuteActionSequenceAsync(ActionSequenceMacro sequence)
    {
        if (_isExecutingActionSequence || _isExecutingMacro)
        {
            return;
        }

        _isExecutingActionSequence = true;
        await _windowActionLock.WaitAsync();
        var completedActions = 0;

        try
        {
            if (!await WaitForShortcutReleaseAsync(sequence.Trigger))
            {
                SetStatus(
                    $"Relâchez complètement {sequence.Trigger.Name} avant de relancer cette séquence.",
                    StatusKind.Warning);
                return;
            }

            foreach (var action in sequence.Actions)
            {
                if (!SendKeyboardAction(action))
                {
                    break;
                }

                completedActions++;
                await Task.Delay(ActionSequenceDelayMs);
            }
        }
        finally
        {
            _isExecutingActionSequence = false;
            _windowActionLock.Release();
        }

        SetStatus(
            completedActions == sequence.Actions.Count
                ? $"Séquence {sequence.Trigger.Name} exécutée."
                : $"La séquence {sequence.Trigger.Name} s’est arrêtée après {completedActions} action(s).",
            completedActions == sequence.Actions.Count ? StatusKind.Ready : StatusKind.Error);
    }

    private static bool SendKeyboardAction(KeyChoice action)
    {
        var modifiers = GetModifierVirtualKeys(action.Modifiers);
        var inputs = new NativeMethods.Input[2 + (modifiers.Count * 2)];
        var inputIndex = 0;

        foreach (var modifier in modifiers)
        {
            inputs[inputIndex++] = CreateKeyboardInput(modifier, false);
        }

        inputs[inputIndex++] = CreateKeyboardInput(action.VirtualKey, false);
        inputs[inputIndex++] = CreateKeyboardInput(action.VirtualKey, true);

        for (var index = modifiers.Count - 1; index >= 0; index--)
        {
            inputs[inputIndex++] = CreateKeyboardInput(modifiers[index], true);
        }

        var sentCount = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());
        if (sentCount == (uint)inputs.Length)
        {
            return true;
        }

        ReleaseGeneratedKeys(action, modifiers);
        return false;
    }

    private static void ReleaseGeneratedKeys(KeyChoice action, IReadOnlyList<int> modifiers)
    {
        var releases = new NativeMethods.Input[1 + modifiers.Count];
        releases[0] = CreateKeyboardInput(action.VirtualKey, true);
        for (var index = 0; index < modifiers.Count; index++)
        {
            releases[index + 1] = CreateKeyboardInput(modifiers[index], true);
        }

        NativeMethods.SendInput(
            (uint)releases.Length,
            releases,
            Marshal.SizeOf<NativeMethods.Input>());
    }

    private static NativeMethods.Input CreateKeyboardInput(int virtualKey, bool isKeyUp)
    {
        var flags = IsExtendedKey(virtualKey) ? NativeMethods.KeyEventExtendedKey : 0;
        if (isKeyUp)
        {
            flags |= NativeMethods.KeyEventKeyUp;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    Flags = flags
                }
            }
        };
    }

    private static List<int> GetModifierVirtualKeys(HotkeyModifiers modifiers)
    {
        var keys = new List<int>();
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            keys.Add(0x11);
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            keys.Add(0x12);
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            keys.Add(0x10);
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            keys.Add(0x5B);
        }

        return keys;
    }

    private static bool IsExtendedKey(int virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x5B or 0x5C;

    private List<nint> ResolveMacroTargets(ClickMacro macro)
    {
        if (macro.ApplyToMatchingWindows)
        {
            var groupOwnHandle = new WindowInteropHelper(this).Handle;
            var groupWindows = WindowService.EnumerateVisibleWindows(groupOwnHandle);
            var selectedHandles = _selectedWindows.Select(window => window.Handle).ToHashSet();
            return groupWindows
                .Where(window => window.ProcessName.Equals(macro.ProcessName, StringComparison.CurrentCultureIgnoreCase))
                .Where(window => selectedHandles.Contains(window.Handle) || _rememberedWindowSelections.Any(saved =>
                    saved.ProcessName.Equals(window.ProcessName, StringComparison.CurrentCultureIgnoreCase) &&
                    TitlesLikelyMatch(saved.Title, window.Title)))
                .Select(window => window.Handle)
                .Distinct()
                .ToList();
        }

        if (NativeMethods.IsWindow(macro.WindowHandle))
        {
            return [macro.WindowHandle];
        }

        var ownHandle = new WindowInteropHelper(this).Handle;
        var currentWindows = WindowService.EnumerateVisibleWindows(ownHandle);
        var replacement = FindBestWindow(currentWindows, macro.ProcessName, macro.WindowTitle);
        return replacement is null ? [] : [replacement.Handle];
    }

    private void EditMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_isCapturing || sender is not Button { Tag: ClickMacro macro })
        {
            return;
        }

        var target = _windows.FirstOrDefault(window => window.Handle == macro.WindowHandle)
                     ?? FindBestWindow(macro.ProcessName, macro.WindowTitle);

        if (target is null)
        {
            MessageBox.Show(
                this,
                "La fenêtre de référence n’est plus ouverte. Ouvrez-la puis actualisez la liste avant de modifier cette macro.",
                "Fenêtre indisponible",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_editingMacro is not null)
        {
            ResetEditState(true);
        }

        if (!target.IsSelected)
        {
            target.IsSelected = true;
            RebuildSelectedWindows();
        }

        _macroKeyBeforeEdit = _macroKey;
        _targetHandleBeforeEdit = (MacroTargetCombo.SelectedItem as WindowItem)?.Handle;
        _applyToMatchingWindowsBeforeEdit = ApplyAllMatchingToggle.IsChecked == true;
        _editingMacro = macro;
        _macroKey = new KeyChoice(macro.KeyName, macro.VirtualKey, macro.Modifiers);
        MacroKeyTextBox.Text = macro.KeyName;
        MacroTargetCombo.SelectedItem = _selectedWindows.FirstOrDefault(window => window.Handle == target.Handle) ?? target;
        ApplyAllMatchingToggle.IsChecked = macro.ApplyToMatchingWindows;
        CaptureButton.Content = "Repositionner le clic";
        CancelEditButton.Visibility = Visibility.Visible;
        SetStatus($"Modification de la macro {macro.KeyName} : ajustez les paramètres puis montrez le nouveau point.", StatusKind.Capture);
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        ResetEditState(true);
        SetStatus("Modification annulée.", StatusKind.Warning);
    }

    private void ResetEditState(bool restorePreviousForm = false)
    {
        if (restorePreviousForm && _macroKeyBeforeEdit is not null)
        {
            _macroKey = _macroKeyBeforeEdit;
            MacroKeyTextBox.Text = _macroKey.Name;
            MacroTargetCombo.SelectedItem = _selectedWindows.FirstOrDefault(window => window.Handle == _targetHandleBeforeEdit)
                                            ?? _selectedWindows.FirstOrDefault();
            ApplyAllMatchingToggle.IsChecked = _applyToMatchingWindowsBeforeEdit;
        }

        _editingMacro = null;
        _macroKeyBeforeEdit = null;
        _targetHandleBeforeEdit = null;
        CaptureButton.Content = "Montrer le point";
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClickMacro macro })
        {
            if (_editingMacro == macro)
            {
                ResetEditState(true);
            }

            _macros.Remove(macro);
            SetStatus($"Macro {macro.KeyName} supprimée.", StatusKind.Warning);
            ScheduleAutoSave();
        }
    }

    private void AddActionSequence_Click(object sender, RoutedEventArgs e)
    {
        if (!KeySequenceParser.TryParse(SequenceActionsTextBox.Text, out var actions, out var error))
        {
            SetStatus(error, StatusKind.Error);
            return;
        }

        if (TriggerConflicts(_sequenceTrigger, _editingActionSequence))
        {
            SetStatus($"{_sequenceTrigger.Name} est déjà utilisé comme raccourci.", StatusKind.Error);
            return;
        }

        var sequence = new ActionSequenceMacro
        {
            Trigger = _sequenceTrigger,
            Actions = actions
        };

        var editedIndex = _editingActionSequence is null ? -1 : _actionSequences.IndexOf(_editingActionSequence);
        if (editedIndex >= 0)
        {
            _actionSequences[editedIndex] = sequence;
            SetStatus($"Séquence {_sequenceTrigger.Name} modifiée.", StatusKind.Ready);
        }
        else
        {
            _actionSequences.Add(sequence);
            SetStatus($"Séquence {_sequenceTrigger.Name} ajoutée.", StatusKind.Ready);
        }

        ResetActionSequenceEditState();
        ScheduleAutoSave();
    }

    private void EditActionSequence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ActionSequenceMacro sequence })
        {
            return;
        }

        if (_editingActionSequence is not null)
        {
            ResetActionSequenceEditState(true);
        }

        _sequenceTriggerBeforeEdit = _sequenceTrigger;
        _sequenceActionsBeforeEdit = SequenceActionsTextBox.Text;
        _editingActionSequence = sequence;
        _sequenceTrigger = sequence.Trigger;
        SequenceTriggerTextBox.Text = sequence.Trigger.Name;
        SequenceActionsTextBox.Text = string.Join(", ", sequence.Actions.Select(action => action.Name));
        AddSequenceButton.Content = "Enregistrer";
        CancelSequenceEditButton.Visibility = Visibility.Visible;
        SetStatus($"Modification de la séquence {sequence.Trigger.Name}.", StatusKind.Capture);
    }

    private void CancelActionSequenceEdit_Click(object sender, RoutedEventArgs e)
    {
        ResetActionSequenceEditState(true);
        SetStatus("Modification de la séquence annulée.", StatusKind.Warning);
    }

    private void DeleteActionSequence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ActionSequenceMacro sequence })
        {
            return;
        }

        if (_editingActionSequence == sequence)
        {
            ResetActionSequenceEditState(true);
        }

        _actionSequences.Remove(sequence);
        SetStatus($"Séquence {sequence.Trigger.Name} supprimée.", StatusKind.Warning);
        ScheduleAutoSave();
    }

    private void ResetActionSequenceEditState(bool restorePreviousForm = false)
    {
        if (restorePreviousForm && _sequenceTriggerBeforeEdit is not null)
        {
            _sequenceTrigger = _sequenceTriggerBeforeEdit;
            SequenceActionsTextBox.Text = _sequenceActionsBeforeEdit ?? string.Empty;
        }
        else
        {
            _sequenceTrigger = FindAvailableSequenceTrigger();
            SequenceActionsTextBox.Text = "T, Ctrl+V, Entrée";
        }

        SequenceTriggerTextBox.Text = _sequenceTrigger.Name;
        _editingActionSequence = null;
        _sequenceTriggerBeforeEdit = null;
        _sequenceActionsBeforeEdit = null;
        AddSequenceButton.Content = "Ajouter";
        CancelSequenceEditButton.Visibility = Visibility.Collapsed;
    }

    private async void LoadConfiguration_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Charger un profil V Macro Keyboard",
            Filter = "Profils V Macro Keyboard (*.vmacro.json)|*.vmacro.json|Anciens profils MacroFenêtre (*.macrofenetre.json)|*.macrofenetre.json|Fichiers JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var configuration = await ConfigurationService.LoadAsync(dialog.FileName);
            ApplyConfiguration(configuration);
            CancelPendingAutoSave();
            await ConfigurationService.SaveAsync(ConfigurationService.AutoSavePath, BuildConfiguration());
            SetStatus(
                $"Profil chargé : {Path.GetFileName(dialog.FileName)} — {_macros.Count} clic(s), {_actionSequences.Count} séquence(s).",
                StatusKind.Ready);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"Le profil n’a pas pu être chargé.\n\n{exception.Message}",
                "Profil invalide",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer le profil V Macro Keyboard",
            Filter = "Profils V Macro Keyboard (*.vmacro.json)|*.vmacro.json|Fichiers JSON (*.json)|*.json",
            DefaultExt = ".vmacro.json",
            AddExtension = true,
            FileName = "mes-macros.vmacro.json",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            CancelPendingAutoSave();
            await ConfigurationService.SaveAsync(dialog.FileName, BuildConfiguration());
            await ConfigurationService.SaveAsync(ConfigurationService.AutoSavePath, BuildConfiguration());
            SetStatus($"Profil enregistré : {Path.GetFileName(dialog.FileName)}", StatusKind.Ready);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                $"Le profil n’a pas pu être enregistré.\n\n{exception.Message}",
                "Enregistrement impossible",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private MacroConfiguration BuildConfiguration()
    {
        RememberCurrentWindowSelections();
        return new MacroConfiguration
        {
            SwitchKey = KeyConfiguration.From(_switchKey),
            SelectedWindows = _rememberedWindowSelections
                .Select(window => new WindowConfiguration
                {
                    ProcessName = window.ProcessName,
                    Title = window.Title
                })
                .ToList(),
            Macros = _macros
                .Select(macro => new ClickMacroConfiguration
                {
                    Key = new KeyConfiguration
                    {
                        Name = macro.KeyName,
                        VirtualKey = macro.VirtualKey,
                        Modifiers = macro.Modifiers
                    },
                    WindowTitle = macro.WindowTitle,
                    ProcessName = macro.ProcessName,
                    RelativeX = macro.RelativeX,
                    RelativeY = macro.RelativeY,
                    ClientX = macro.ClientX,
                    ClientY = macro.ClientY,
                    ReferenceWidth = macro.ReferenceWidth,
                    ReferenceHeight = macro.ReferenceHeight,
                    ApplyToMatchingWindows = macro.ApplyToMatchingWindows
                })
                .ToList(),
            ActionSequences = _actionSequences
                .Select(sequence => new ActionSequenceConfiguration
                {
                    Trigger = InputTriggerConfiguration.From(sequence.Trigger),
                    Actions = sequence.Actions.Select(KeyConfiguration.From).ToList()
                })
                .ToList(),
            ActionSequenceDelayMs = ActionSequenceDelayMs,
            StabilizationDelayMs = StabilizationDelayMs,
            RestoreCursor = RestoreCursorToggle.IsChecked == true,
            ShortcutsEnabled = MasterToggle.IsChecked == true
        };
    }

    private void ApplyConfiguration(MacroConfiguration configuration)
    {
        _isLoadingConfiguration = true;
        _isRefreshingWindows = true;
        try
        {
            ResetEditState();
            ResetActionSequenceEditState();
            _switchKey = configuration.SwitchKey.ToKeyChoice(KeyChoice.F8);
            SwitchKeyTextBox.Text = _switchKey.Name;
            StabilizationDelaySlider.Value = Math.Clamp(configuration.StabilizationDelayMs, 100, 1200);
            ActionSequenceDelaySlider.Value = Math.Clamp(configuration.ActionSequenceDelayMs, 30, 500);
            RestoreCursorToggle.IsChecked = configuration.RestoreCursor;
            MasterToggle.IsChecked = configuration.ShortcutsEnabled;
            _shortcutsEnabled = configuration.ShortcutsEnabled;

            _rememberedWindowSelections.Clear();
            _rememberedWindowSelections.AddRange(configuration.SelectedWindows.Select(saved => new WindowConfiguration
            {
                ProcessName = saved.ProcessName,
                Title = saved.Title
            }));

            foreach (var window in _windows)
            {
                window.IsSelected = configuration.SelectedWindows.Any(saved =>
                    saved.ProcessName.Equals(window.ProcessName, StringComparison.CurrentCultureIgnoreCase) &&
                    TitlesLikelyMatch(saved.Title, window.Title));
            }

            _macros.Clear();
            var gestures = new HashSet<(int VirtualKey, HotkeyModifiers Modifiers)>();
            foreach (var savedMacro in configuration.Macros ?? [])
            {
                var key = savedMacro.Key.ToKeyChoice(KeyChoice.F6);
                if (key.Matches(_switchKey.VirtualKey, _switchKey.Modifiers) ||
                    !gestures.Add((key.VirtualKey, key.Modifiers)) ||
                    string.IsNullOrWhiteSpace(savedMacro.ProcessName))
                {
                    continue;
                }

                var target = FindBestWindow(savedMacro.ProcessName, savedMacro.WindowTitle);
                _macros.Add(new ClickMacro
                {
                    VirtualKey = key.VirtualKey,
                    Modifiers = key.Modifiers,
                    KeyName = key.Name,
                    WindowHandle = target?.Handle ?? nint.Zero,
                    WindowTitle = savedMacro.WindowTitle,
                    ProcessName = savedMacro.ProcessName,
                    RelativeX = Math.Clamp(savedMacro.RelativeX, 0, 1),
                    RelativeY = Math.Clamp(savedMacro.RelativeY, 0, 1),
                    ClientX = Math.Max(0, savedMacro.ClientX),
                    ClientY = Math.Max(0, savedMacro.ClientY),
                    ReferenceWidth = Math.Max(0, savedMacro.ReferenceWidth),
                    ReferenceHeight = Math.Max(0, savedMacro.ReferenceHeight),
                    ApplyToMatchingWindows = savedMacro.ApplyToMatchingWindows
                });
            }

            _actionSequences.Clear();
            foreach (var savedSequence in configuration.ActionSequences ?? [])
            {
                var trigger = savedSequence.Trigger?.ToInputTrigger();
                var actions = (savedSequence.Actions ?? [])
                    .Where(action => action.VirtualKey != 0)
                    .Select(action => action.ToKeyChoice(KeyChoice.F6))
                    .ToList();

                if (trigger is null ||
                    trigger.Code == 0 ||
                    actions.Count == 0 ||
                    TriggerConflicts(trigger))
                {
                    continue;
                }

                _actionSequences.Add(new ActionSequenceMacro
                {
                    Trigger = trigger,
                    Actions = actions
                });
            }

            if (_macros.Any(macro => GestureEquals(macro, _macroKey)) ||
                _actionSequences.Any(sequence =>
                    sequence.Trigger.MatchesKeyboard(_macroKey.VirtualKey, _macroKey.Modifiers)))
            {
                _macroKey = FindAvailableDefaultKey();
                MacroKeyTextBox.Text = _macroKey.Name;
            }

            ResetActionSequenceEditState();
        }
        finally
        {
            _isRefreshingWindows = false;
            _isLoadingConfiguration = false;
        }

        RebuildSelectedWindows();
    }

    private KeyChoice FindAvailableDefaultKey()
    {
        for (var number = 1; number <= 12; number++)
        {
            var key = new KeyChoice($"F{number}", 0x6F + number);
            if (!_switchKey.Matches(key.VirtualKey, key.Modifiers) &&
                !_macros.Any(macro => GestureEquals(macro, key)) &&
                !_actionSequences.Any(sequence =>
                    sequence.Trigger.MatchesKeyboard(key.VirtualKey, key.Modifiers)))
            {
                return key;
            }
        }

        return new KeyChoice("Ctrl + Maj + M", 0x4D, HotkeyModifiers.Control | HotkeyModifiers.Shift);
    }

    private InputTrigger FindAvailableSequenceTrigger()
    {
        for (var number = 1; number <= 24; number++)
        {
            var trigger = InputTrigger.Keyboard(new KeyChoice($"F{number}", 0x6F + number));
            if (!TriggerConflicts(trigger))
            {
                return trigger;
            }
        }

        return InputTrigger.Keyboard(new KeyChoice(
            "Ctrl + Alt + Maj + M",
            0x4D,
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift));
    }

    private WindowItem? FindBestWindow(string processName, string title) =>
        FindBestWindow(_windows, processName, title);

    private static WindowItem? FindBestWindow(IEnumerable<WindowItem> windows, string processName, string title)
    {
        var candidates = windows
            .Where(window => window.ProcessName.Equals(processName, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        return candidates.FirstOrDefault(window => window.Title.Equals(title, StringComparison.CurrentCultureIgnoreCase))
               ?? candidates.FirstOrDefault(window => TitlesLikelyMatch(window.Title, title))
               ?? (candidates.Length == 1 ? candidates[0] : null);
    }

    private static bool TitlesLikelyMatch(string first, string second)
    {
        if (first.Equals(second, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        var normalizedFirst = NormalizeWindowTitle(first);
        var normalizedSecond = NormalizeWindowTitle(second);
        return normalizedFirst.Length >= 4 && normalizedSecond.Length >= 4 &&
               (normalizedFirst.Equals(normalizedSecond, StringComparison.CurrentCultureIgnoreCase) ||
                normalizedFirst.Contains(normalizedSecond, StringComparison.CurrentCultureIgnoreCase) ||
                normalizedSecond.Contains(normalizedFirst, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string NormalizeWindowTitle(string title)
    {
        string[] knownSuffixes =
        [
            " - Microsoft Word", " - Word", " - Google Chrome", " - Microsoft Edge",
            " — Mozilla Firefox", " - Mozilla Firefox", " - Excel", " - PowerPoint"
        ];

        var normalized = title.Trim().TrimStart('*').Trim();
        foreach (var suffix in knownSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.CurrentCultureIgnoreCase))
            {
                normalized = normalized[..^suffix.Length].Trim();
                break;
            }
        }

        return normalized;
    }

    private void PersistentSetting_Changed(object sender, RoutedEventArgs e) => ScheduleAutoSave();

    private void StabilizationDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StabilizationDelayText is not null)
        {
            StabilizationDelayText.Text = $"{(int)Math.Round(e.NewValue)} ms";
        }

        ScheduleAutoSave();
    }

    private void ActionSequenceDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ActionSequenceDelayText is not null)
        {
            ActionSequenceDelayText.Text = $"{(int)Math.Round(e.NewValue)} ms";
        }

        ScheduleAutoSave();
    }

    private int StabilizationDelayMs => (int)Math.Round(StabilizationDelaySlider.Value);
    private int ActionSequenceDelayMs => (int)Math.Round(ActionSequenceDelaySlider.Value);

    private void ScheduleAutoSave()
    {
        if (!_isLoaded || _isLoadingConfiguration)
        {
            return;
        }

        RememberCurrentWindowSelections();
        _autoSaveCancellation?.Cancel();
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void RememberCurrentWindowSelections()
    {
        foreach (var visibleWindow in _windows)
        {
            _rememberedWindowSelections.RemoveAll(saved =>
                saved.ProcessName.Equals(visibleWindow.ProcessName, StringComparison.CurrentCultureIgnoreCase) &&
                TitlesLikelyMatch(saved.Title, visibleWindow.Title));
        }

        foreach (var selectedWindow in _selectedWindows)
        {
            if (_rememberedWindowSelections.Any(saved =>
                    saved.ProcessName.Equals(selectedWindow.ProcessName, StringComparison.CurrentCultureIgnoreCase) &&
                    TitlesLikelyMatch(saved.Title, selectedWindow.Title)))
            {
                continue;
            }

            _rememberedWindowSelections.Add(new WindowConfiguration
            {
                ProcessName = selectedWindow.ProcessName,
                Title = selectedWindow.Title
            });
        }
    }

    private async void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        var cancellation = new CancellationTokenSource();
        _autoSaveCancellation = cancellation;
        try
        {
            await ConfigurationService.SaveAsync(
                ConfigurationService.AutoSavePath,
                BuildConfiguration(),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer change or application shutdown superseded this save.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Sauvegarde automatique impossible : {exception.Message}", StatusKind.Warning);
        }
        finally
        {
            if (ReferenceEquals(_autoSaveCancellation, cancellation))
            {
                _autoSaveCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelPendingAutoSave()
    {
        _autoSaveTimer.Stop();
        _autoSaveCancellation?.Cancel();
    }

    private HotkeyModifiers GetPressedModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsKeyDown(0x10) || IsKeyDown(0xA0) || IsKeyDown(0xA1))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsKeyDown(0x11) || IsKeyDown(0xA2) || IsKeyDown(0xA3))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsKeyDown(0x12) || IsKeyDown(0xA4) || IsKeyDown(0xA5))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsKeyDown(0x5B) || IsKeyDown(0x5C))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool TryGetModifier(int virtualKey, out HotkeyModifiers modifier)
    {
        modifier = virtualKey switch
        {
            0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Control,
            0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
            0x5B or 0x5C => HotkeyModifiers.Windows,
            _ => HotkeyModifiers.None
        };
        return modifier != HotkeyModifiers.None;
    }

    private static HotkeyModifiers ToHotkeyModifiers(ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }

    private static Key ResolveKey(KeyEventArgs eventArgs) => eventArgs.Key switch
    {
        Key.System => eventArgs.SystemKey,
        Key.ImeProcessed => eventArgs.ImeProcessedKey,
        Key.DeadCharProcessed => eventArgs.DeadCharProcessedKey,
        _ => eventArgs.Key
    };

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string FormatKeyChoice(Key key, HotkeyModifiers modifiers) =>
        $"{FormatModifiers(modifiers)}{GetKeyName(key)}";

    private static string FormatModifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Maj");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Windows");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" + ", parts) + " + ";
    }

    private static string GetKeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)key - (int)Key.D0).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return $"Pavé {((int)key - (int)Key.NumPad0)}";
        }

        return key switch
        {
            Key.Return => "Entrée",
            Key.Escape => "Échap",
            Key.Space => "Espace",
            Key.Back => "Retour arrière",
            Key.Delete => "Suppr",
            Key.Insert => "Inser",
            Key.Capital => "Verr. Maj",
            Key.NumLock => "Verr. Num",
            Key.Scroll => "Arrêt défil",
            Key.Snapshot => "Impr. écran",
            Key.Prior => "Page précédente",
            Key.Next => "Page suivante",
            Key.LeftCtrl => "Ctrl gauche",
            Key.RightCtrl => "Ctrl droit",
            Key.LeftShift => "Maj gauche",
            Key.RightShift => "Maj droite",
            Key.LeftAlt => "Alt gauche",
            Key.RightAlt => "Alt droit",
            Key.LWin => "Windows gauche",
            Key.RWin => "Windows droit",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            _ => key.ToString()
        };
    }

    private static bool GestureEquals(ClickMacro macro, KeyChoice choice) =>
        macro.VirtualKey == choice.VirtualKey && macro.Modifiers == choice.Modifiers;

    private bool TriggerConflicts(
        InputTrigger trigger,
        ActionSequenceMacro? excludedSequence = null)
    {
        if (trigger.Kind == InputTriggerKind.Keyboard &&
            (_switchKey.Matches(trigger.Code, trigger.Modifiers) ||
             _macros.Any(macro =>
                 macro.VirtualKey == trigger.Code && macro.Modifiers == trigger.Modifiers)))
        {
            return true;
        }

        return _actionSequences.Any(sequence =>
            sequence != excludedSequence &&
            sequence.Trigger.Kind == trigger.Kind &&
            sequence.Trigger.Code == trigger.Code &&
            sequence.Trigger.Modifiers == trigger.Modifiers);
    }

    private void RebuildShortcutIndexes()
    {
        _clickMacrosByGesture.Clear();
        foreach (var macro in _macros)
        {
            _clickMacrosByGesture.TryAdd((macro.VirtualKey, macro.Modifiers), macro);
        }

        _sequencesByGesture.Clear();
        _sequencesByMouseButton.Clear();
        foreach (var sequence in _actionSequences)
        {
            if (sequence.Trigger.Kind == InputTriggerKind.Keyboard)
            {
                _sequencesByGesture.TryAdd(
                    (sequence.Trigger.Code, sequence.Trigger.Modifiers),
                    sequence);
            }
            else
            {
                _sequencesByMouseButton.TryAdd(sequence.Trigger.Code, sequence);
            }
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        StatusDot.Fill = new SolidColorBrush(kind switch
        {
            StatusKind.Ready => Color.FromRgb(54, 179, 126),
            StatusKind.Warning => Color.FromRgb(255, 171, 0),
            StatusKind.Error => Color.FromRgb(222, 53, 11),
            StatusKind.Capture => Color.FromRgb(110, 63, 144),
            _ => Color.FromRgb(100, 112, 137)
        });
    }

    private enum StatusKind
    {
        Ready,
        Warning,
        Error,
        Capture
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Force.Halo.Checkpoints.Linux.Services;
using SDL2;

namespace Force.Halo.Checkpoints.Linux.Views;

// If you are debugging this with PINCE, MCC's name appears as "MAIN_THREAD"!

public partial class MainWindow : Window
{
    private string _gameSelected = "none";
    private bool _waitingForHotkey;
    private Key? _hotkey;
    private KeyModifiers _hotkeyModifiers;
    private GlobalHotkeyPortal? _globalHotkeyPortal;
    private bool _globalHotkeyBound;
    private string? _globalHotkeyDescription;
    private X11HotkeyListener? _x11HotkeyListener;
    private bool _x11HotkeyBound;
    private string? _x11HotkeyDescription;
    private GamepadHotkeyService? _gamepadService;
    private string? _gamepadBindingDescription;
    private HotkeySettings _hotkeySettings = HotkeySettings.Empty;
    private bool _x11DisplayWarningShown;
    private bool _x11LibWarningShown;
    private bool _x11SymbolWarningShown;

    private const int PtracePeekdata = 2;
    private const int PtracePokedata = 5;
    private const int PtraceAttach = 16;
    private const int PtraceDetach = 17;
    private const int Wuntraced = 2;
    private const int SigCont = 18;

    [DllImport("libc", SetLastError = true)]
    private static extern int ptrace(int request, int pid, IntPtr addr, IntPtr data);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += MainWindow_OnKeyDown;
        Opened += MainWindow_OnOpened;
        Closing += MainWindow_OnClosing;
        Closed += MainWindow_OnClosed;
        UpdateAttachStatus(false, "Not attached. Select a game to try attaching.");
        LoadHotkeySettings();
        UpdateHotkeyText();
        UpdateControllerHotkeyText();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = version != null ? $"V{version.Major}.{version.Minor}.{version.Build}" : "V?";
    }

    private void ShowErrorWindow(string errorMessage)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            ErrorWindow errorWindow = new();
            errorWindow.ErrorTextBlock.Text = errorMessage;

            if (VisualRoot is Window window)
            {
                await errorWindow.ShowDialog(window);
            }
            else
            {
                Console.Write("The error window broke somehow. Please submit an Issue for this in GitHub.");
            }
        });
    }

    private void ModifyGameMemory(string gameName, string processName, string moduleName, long offset, byte value)
    {
        int pid = GetProcessIdByName(processName);
        if (pid == -1)
        {
            ShowErrorWindow($"{gameName} does not appear to be running.");
            UpdateAttachStatus(false, "Not attached. Select a game to try attaching.");
            UpdateCheckpointStatus("Checkpoint status: failed (game not running).");
            return;
        }

        IntPtr baseAddress = GetModuleBaseAddress(pid, moduleName);
        if (baseAddress == IntPtr.Zero)
        {
            ShowErrorWindow($"Failed to find the base address of {moduleName}.");
            DetachProcess(pid);
            UpdateCheckpointStatus("Checkpoint status: failed (module not found).");
            return;
        }

        IntPtr targetAddress = IntPtr.Add(baseAddress, (int)offset);

        // This makes no sense. I don't know why Ptrace has such a goddamned hard time attaching to processes correctly and getting the right goddamned...
        // ... values from offsets, but since we know what data to expect and what outcome we should be getting from attempting to attach to the process,...
        // ... just trying again seems to work well enough. I'm sure this is a result of me not fully understanding Ptrace, but w/e.
        int attachAttempts = 1;
        while (attachAttempts < 1000)
        {
            if (ptrace(PtraceAttach, pid, IntPtr.Zero, IntPtr.Zero) == -1)
            {
                attachAttempts++;
                DetachProcess(pid);
                Thread.Sleep(5);
            }
            else
            {
                break;
            }
        }

        if (attachAttempts == 1000)
        {
            ShowErrorWindow(
                $"Failed to attach to process {pid}. Ensure this program is running as root/sudo. Number of attempts reached: {attachAttempts}.");
            DetachProcess(pid);
            UpdateAttachStatus(false, "Failed to attach. Select a game to retry.");
            UpdateCheckpointStatus("Checkpoint status: failed to attach.");
            return;
        }

        try
        {
            _ = waitpid(pid, out _, Wuntraced);
            // Same explaination as the comment above. Basically, my plan is to try the exact same thing and hope for a different outcome.
            // I've seen the last successful attempt land on like 42 or something, so 60 it is!
            int attempts = 1;
            bool successfullyWroteToMemory = false;
            while (attempts < 60 && !successfullyWroteToMemory)
            {
                // PTRACE_POKEDATA writes a full word (8 bytes on x86_64). To avoid overwriting
                // the 7 adjacent bytes, read the current word first, modify only the target byte,
                // then write the modified word back.
                IntPtr currentWord = ptrace(PtracePeekdata, pid, targetAddress, IntPtr.Zero);
                long wordValue = currentWord.ToInt64();
                wordValue = (wordValue & ~0xFFL) | (long)value;
                IntPtr modifiedWord = new IntPtr(wordValue);

                if (ptrace(PtracePokedata, pid, targetAddress, modifiedWord) != -1)
                {
                    successfullyWroteToMemory = true;
                    break;
                }

                attempts++;
                if (attempts >= 60)
                {
                    ShowErrorWindow("Failed to force the checkpoint after 60 attempts. " +
                                    "Make sure the game is running and you are running this program as root/sudo.");
                    DetachProcess(pid);
                    UpdateCheckpointStatus("Checkpoint status: failed after 60 attempts.");
                    return;
                }
            }

            if (successfullyWroteToMemory)
            {
                UpdateCheckpointStatus($"Checkpoint status: success after {attempts} attempt(s).");
                UpdateAttachStatus(true, $"{gameName} attach check: success.");
            }
        }
        finally
        {
            DetachProcess(pid);
        }
    }

    private static int GetProcessIdByName(string processName)
    {
        int
            lastPid = -1; // For whatever reason, this seems to be reliable for Proton. Don't know about anything else. I should probably find a better method.
        string[] procEntries = Directory.GetDirectories("/proc");

        foreach (string entry in procEntries)
        {
            if (!int.TryParse(Path.GetFileName(entry), out int pid)) continue;
            try
            {
                string cmdline = File.ReadAllText($"/proc/{pid}/cmdline");
                if (cmdline.Contains(processName))
                {
                    lastPid = pid;
                }
            }
            catch
            {
                // Ignore processes we can't read and hope it's not important. :)
            }
        }

        return lastPid;
    }

    private static IntPtr GetModuleBaseAddress(int pid, string moduleName)
    {
        string mapsPath = $"/proc/{pid}/maps";
        if (!File.Exists(mapsPath))
        {
            return IntPtr.Zero;
        }

        var lines = File.ReadAllLines(mapsPath);
        foreach (var line in lines)
        {
            if (!line.Contains(moduleName)) continue;
            var parts = line.Split('-');
            if (parts.Length > 0 && long.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null,
                    out long address))
            {
                return new IntPtr(address);
            }
        }

        return IntPtr.Zero;
    }

    private void ForceCheckpointButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_gameSelected == "none")
        {
            ShowErrorWindow("No game selected.");
            UpdateAttachStatus(false, "Not attached. Select a game to try attaching.");
            UpdateCheckpointStatus("Checkpoint status: failed (no game selected).");
        }
        else
        {
            string gameSelected = _gameSelected;
            Task.Run(() =>
            {
                try
                {
                    if (gameSelected == "OGHaloCE")
                    {
                        ModifyGameMemory("Halo CE (non-MCC)", "halo.exe", "halo.exe", 0x31973F, 1);
                    }
                    else if (gameSelected == "MCC")
                    {
                        string gameName = CheckWhichMCCGameIsRunning(true);
                        switch (gameName)
                        {
                            case "Halo CE":
                                ModifyGameMemory("MCC", "MCC-Win64-Shipping.exe", "halo1.dll", 0x2B23707, 1);
                                break;
                            case "Halo 2":
                                ModifyGameMemory("MCC", "MCC-Win64-Shipping.exe", "halo2.dll", 0xE70D7E, 1);
                                break;
                            case "Halo 3":
                                ModifyGameMemory("MCC", "MCC-Win64-Shipping.exe", "halo3.dll", 0x20B96AC, 1);
                                break;
                            case "Halo 4":
                                ModifyGameMemory("MCC", "MCC-Win64-Shipping.exe", "halo4.dll", 0x293DEAF, 1);
                                break;
                            case "Halo 3: ODST":
                                ModifyGameMemory("MCC", "MCC-Win64-Shipping.exe", "halo3odst.dll", 0x20FF6BC, 1);
                                break;
                            case "Halo: Reach":
                                ModifyGameMemory("MCC", "MCC-Win64-Shipping.exe", "haloreach.dll", 0x263EABE, 1);
                                break;
                            case "none":
                                UpdateAttachStatus(false, "Not attached. Select a game to retry.");
                                UpdateCheckpointStatus("Checkpoint status: failed (no MCC game detected).");
                                break;
                            default:
                                ShowErrorWindow($"What's the meaning of this? Who are you? How did you get in here?");
                                return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShowErrorWindow($"Failed to force checkpoint: {ex.Message}");
                    UpdateCheckpointStatus("Checkpoint status: failed (exception).");
                }
            });
        }
    }

    private void HaloCEButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _gameSelected = "OGHaloCE";
        Task.Run(TryAttachToSelectedGame);
    }

    private void MCCButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _gameSelected = "MCC";
        Task.Run(TryAttachToSelectedGame);
    }

    private string CheckWhichMCCGameIsRunning(bool showErrors)
    {
        int attempts = 1;
        int pid = GetProcessIdByName("MCC-Win64-Shipping.exe");
        if (pid == -1)
        {
            if (showErrors)
            {
                ShowErrorWindow($"The Master Chief Collection does not appear to be running.");
            }
            return "none";
        }
        
        while (attempts < 31)
        {
            int gameNumber = 0;
            while (gameNumber < 6)
            {
                gameNumber++;

                // I've already learned my lesson: never trust a published build. I'm not making these empty
                // ... nor null in case the published build decides to make my life miserable.
                string gameName = "gameNameEmpty";
                string moduleName = "moduleNameEmpty";
                int offset = 0;
                int expectedIntValue = 1;

                switch (gameNumber)
                {
                    case 1:
                        gameName = "Halo CE";
                        moduleName = "halo1.dll";
                        offset = 0x2DCF80B;
                        expectedIntValue = 1550281062;
                        break;
                    case 2:
                        gameName = "Halo 2";
                        moduleName = "mss64dsp.flt";
                        offset = 0x24690;
                        break;
                    case 3:
                        gameName = "Halo 3";
                        moduleName = "halo3.dll";
                        offset = 0x46BCD0C;
                        break;
                    case 4:
                        gameName = "Halo 4";
                        moduleName = "halo4.dll";
                        offset = 0xE3B005;
                        break;
                    case 5:
                        gameName = "Halo 3: ODST";
                        moduleName = "halo3odst.dll";
                        offset = 0x2174F43;
                        expectedIntValue = 862940257;
                        break;
                    case 6:
                        gameName = "Halo: Reach";
                        moduleName = "haloreach.dll";
                        offset = 0xAFBC30;
                        expectedIntValue = 1667327346;
                        break;
                    default:
                        if (showErrors)
                        {
                            ShowErrorWindow($"No games in The Master Chief Collection appear to be running.");
                        }
                        DetachProcess(pid);
                        return "none";
                }
                
                // Get boring address stuff blah blah blah
                IntPtr baseAddress = GetModuleBaseAddress(pid, moduleName);
                if (baseAddress == IntPtr.Zero)
                {
                    DetachProcess(pid);
                    continue;
                }

                IntPtr targetAddress = IntPtr.Add(baseAddress, offset);
                try
                {
                    if (ptrace(PtraceAttach, pid, IntPtr.Zero, IntPtr.Zero) == -1)
                    {
                        DetachProcess(pid);
                        continue;
                    }
                    _ = waitpid(pid, out _, Wuntraced);

                    // Having the dll load isn't sufficient enough. We need to check out the value from the targeted address...
                    // ... and make sure it returns a value only when the respective game is running (or loading, shhhhhh)
                    IntPtr result = ptrace(PtracePeekdata, pid, targetAddress, IntPtr.Zero);
                    long int32Value = result.ToInt64();
                    byte[] bytes = BitConverter.GetBytes(result.ToInt64());
                    int boolValue = bytes[0];
                    if (result.ToInt64() == 1 || int32Value == expectedIntValue || boolValue == expectedIntValue)
                    {
                        DetachProcess(pid);
                        return gameName;
                    }
                    else
                    {
                        Thread.Sleep(2);
                    }
                }
                finally
                {
                    DetachProcess(pid);
                }
            }
            attempts++;
        }

        if (showErrors)
        {
            ShowErrorWindow(
                $"No games in The Master Chief Collection appear to be running or the game process is occupied by another program, such as PINCE.");
        }

        return "none";
    }

    private void TryAttachToSelectedGame()
    {
        if (_gameSelected == "none")
        {
            UpdateAttachStatus(false, "Not attached. Select a game to try attaching.");
            return;
        }

        if (_gameSelected == "OGHaloCE")
        {
            TryAttachToProcess("halo.exe", "Halo CE (non-MCC)");
            return;
        }

        if (_gameSelected == "MCC")
        {
            int pid = GetProcessIdByName("MCC-Win64-Shipping.exe");
            if (pid == -1)
            {
                UpdateAttachStatus(false, "MCC not running. Select a game to retry.");
                return;
            }

            string gameName = CheckWhichMCCGameIsRunning(false);
            if (gameName == "none")
            {
                UpdateAttachStatus(false, "No MCC game detected. Select a game to retry.");
                return;
            }

            TryAttachToProcess("MCC-Win64-Shipping.exe", $"MCC: {gameName}");
        }
    }

    private void TryAttachToProcess(string processName, string displayName)
    {
        int pid = GetProcessIdByName(processName);
        if (pid == -1)
        {
            UpdateAttachStatus(false, $"{displayName} not running. Select a game to retry.");
            return;
        }

        bool attached = false;
        int attachAttempts = 1;
        while (attachAttempts < 25)
        {
            if (ptrace(PtraceAttach, pid, IntPtr.Zero, IntPtr.Zero) == -1)
            {
                attachAttempts++;
                DetachProcess(pid);
                Thread.Sleep(5);
            }
            else
            {
                attached = true;
                break;
            }
        }

        if (attached)
        {
            _ = waitpid(pid, out _, Wuntraced);
        }
        DetachProcess(pid);

        if (attached)
        {
            UpdateAttachStatus(true, $"{displayName} attach check: success.");
        }
        else
        {
            UpdateAttachStatus(false, "Failed to attach. Select a game to retry.");
        }
    }

    private void UpdateAttachStatus(bool attached, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (AttachStatusText != null)
            {
                AttachStatusText.Text = message;
            }

            if (AttachStatusIndicator != null)
            {
                AttachStatusIndicator.Background = new SolidColorBrush(attached
                    ? Color.Parse("#FF4CAF50")
                    : Color.Parse("#FFC62828"));
            }
        });
    }

    private void UpdateCheckpointStatus(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (CheckpointStatusText != null)
            {
                CheckpointStatusText.Text = message;
            }
        });
    }

    private void ShowWarningWindow(string message, Func<HotkeySettings, HotkeySettings> applySuppression)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            NoticeWindow noticeWindow = new();
            noticeWindow.NoticeTextBlock.Text = message;

            if (VisualRoot is Window window)
            {
                bool? dontShowAgain = await noticeWindow.ShowDialog<bool?>(window);
                if (dontShowAgain == true)
                {
                    _hotkeySettings = applySuppression(_hotkeySettings);
                    SettingsStore.Save(_hotkeySettings);
                }
            }
            else
            {
                Console.Write("The warning window broke somehow. Please submit an Issue for this in GitHub.");
            }
        });
    }

    private void ShowPersistentErrorWindow(string message)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            ErrorWindow errorWindow = new();
            errorWindow.ErrorTextBlock.Text = message;

            if (VisualRoot is Window window)
            {
                await errorWindow.ShowDialog(window);
            }
            else
            {
                Console.Write("The error window broke somehow. Please submit an Issue for this in GitHub.");
            }
        });
    }

    private void SetHotkeyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _waitingForHotkey = true;
        UpdateHotkeyText("Press a key...");
    }

    private void ClearHotkeyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _hotkey = null;
        _hotkeyModifiers = KeyModifiers.None;
        _waitingForHotkey = false;
        _globalHotkeyBound = false;
        _globalHotkeyDescription = null;
        _x11HotkeyBound = false;
        _x11HotkeyDescription = null;
        _hotkeySettings = _hotkeySettings.WithLocal(null, null).WithGlobal(null, null);
        SettingsStore.Save(_hotkeySettings);
        UpdateHotkeyStatus("Global hotkey cleared.");
        _ = ClearGlobalHotkeyAsync();
        ClearX11Hotkey();
        UpdateHotkeyText();
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_waitingForHotkey)
        {
            if (e.Key == Key.Escape)
            {
                _waitingForHotkey = false;
                UpdateHotkeyText();
                e.Handled = true;
                return;
            }

            if (IsModifierKey(e.Key))
            {
                UpdateHotkeyText("Modifier-only keys not supported.");
                e.Handled = true;
                return;
            }

            _waitingForHotkey = false;
            _ = ApplyHotkeySelectionAsync(e.Key, e.KeyModifiers);
            e.Handled = true;
            return;
        }

        if (!_globalHotkeyBound && !_x11HotkeyBound && _hotkey.HasValue && e.Key == _hotkey.Value &&
            e.KeyModifiers == _hotkeyModifiers)
        {
            ForceCheckpointButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin;
    }

    private void UpdateHotkeyText(string? overrideText = null)
    {
        if (HotkeyText == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(overrideText))
        {
            HotkeyText.Text = overrideText;
            return;
        }

        if (_globalHotkeyBound && !string.IsNullOrWhiteSpace(_globalHotkeyDescription))
        {
            HotkeyText.Text = $"{_globalHotkeyDescription} (global)";
            return;
        }

        if (_x11HotkeyBound && !string.IsNullOrWhiteSpace(_x11HotkeyDescription))
        {
            HotkeyText.Text = $"{_x11HotkeyDescription} (global X11)";
            return;
        }

        if (!_hotkey.HasValue)
        {
            HotkeyText.Text = "None";
            return;
        }

        HotkeyText.Text = $"{FormatHotkey(_hotkey.Value, _hotkeyModifiers)} (focused)";
    }

    private static string FormatHotkey(Key key, KeyModifiers modifiers)
    {
        string modifierText = modifiers switch
        {
            KeyModifiers.None => string.Empty,
            _ => modifiers.ToString().Replace(", ", "+") + "+"
        };
        return $"{modifierText}{key}";
    }

    private async void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        await InitializeGlobalHotkeyAsync();
        InitializeGamepadHotkey();
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        SettingsStore.Save(_hotkeySettings);
        _x11HotkeyListener?.Dispose();
        _x11HotkeyListener = null;
        _gamepadService?.Dispose();
        _gamepadService = null;
    }

    private async void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_globalHotkeyPortal != null)
        {
            try
            {
                await _globalHotkeyPortal.DisposeAsync();
            }
            catch (TaskCanceledException)
            {
                // Ignore cancellation during shutdown.
            }
            catch
            {
                // Best-effort cleanup.
            }
            finally
            {
                _globalHotkeyPortal = null;
            }
        }
    }

    private void LoadHotkeySettings()
    {
        _hotkeySettings = SettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(_hotkeySettings.LocalKey) &&
            Enum.TryParse<Key>(_hotkeySettings.LocalKey, out var key))
        {
            _hotkey = key;
        }

        if (!string.IsNullOrWhiteSpace(_hotkeySettings.LocalModifiers) &&
            Enum.TryParse<KeyModifiers>(_hotkeySettings.LocalModifiers, out var modifiers))
        {
            _hotkeyModifiers = modifiers;
        }

        _globalHotkeyDescription = _hotkeySettings.GlobalTriggerDescription;
    }

    private async Task InitializeGlobalHotkeyAsync()
    {
        _globalHotkeyPortal = await GlobalHotkeyPortal.TryCreateAsync();
        if (_globalHotkeyPortal == null)
        {
            _globalHotkeyBound = false;
            UpdateHotkeyStatus("Wayland portal not found. Trying X11 fallback...");
            TryBindX11FromSettings();
            return;
        }

        _globalHotkeyPortal.Activated += () =>
        {
            Dispatcher.UIThread.Post(() => ForceCheckpointButton_OnClick(this, new RoutedEventArgs()));
        };

        UpdateHotkeyStatus("Global hotkey available.");

        if (!string.IsNullOrWhiteSpace(_hotkeySettings.PreferredGlobalTrigger))
        {
            UpdateHotkeyStatus("Rebinding global hotkey...");
            var result = await _globalHotkeyPortal.RebindAsync("Force checkpoint",
                _hotkeySettings.PreferredGlobalTrigger!, string.Empty);
            if (result.Success)
            {
                _globalHotkeyBound = true;
                _globalHotkeyDescription = result.TriggerDescription ?? _hotkeySettings.GlobalTriggerDescription;
                _hotkeySettings = _hotkeySettings.WithGlobal(_hotkeySettings.PreferredGlobalTrigger,
                    _globalHotkeyDescription);
                SettingsStore.Save(_hotkeySettings);
                _x11HotkeyBound = false;
                _x11HotkeyDescription = null;
                ClearX11Hotkey();
                UpdateHotkeyStatus("Global hotkey ready.");
                UpdateHotkeyText();
            }
            else
            {
                _globalHotkeyBound = false;
                _globalHotkeyDescription = null;
                if (result.Message == "Global shortcut setup cancelled.")
                {
                    UpdateHotkeyStatus("Global hotkey setup cancelled.");
                }
                else
                {
                    UpdateHotkeyStatus(result.Message ?? "Global hotkey setup failed. Trying X11 fallback...");
                    TryBindX11FromSettings();
                }
                UpdateHotkeyText();
            }
        }
    }

    private void InitializeGamepadHotkey()
    {
        _gamepadService ??= new GamepadHotkeyService();
        _gamepadService.Status += message =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ControllerHotkeyStatusText != null)
                {
                    ControllerHotkeyStatusText.Text = message;
                }
            });
        };
        _gamepadService.Activated += () =>
        {
            Dispatcher.UIThread.Post(() => ForceCheckpointButton_OnClick(this, new RoutedEventArgs()));
        };
        _gamepadService.Captured += (name, instanceId, button) =>
        {
            _gamepadBindingDescription = GetControllerButtonLabel(button);
            _hotkeySettings = _hotkeySettings.WithControllerBinding((int)button, instanceId, name);
            SettingsStore.Save(_hotkeySettings);
            Dispatcher.UIThread.Post(() =>
            {
                UpdateControllerHotkeyText();
                UpdateControllerHotkeyStatus("Controller hotkey ready.");
            });
        };

        bool started = _gamepadService.Start();
        RestoreGamepadBinding();

        if (!started)
        {
            if (_hotkeySettings.ControllerButton.HasValue)
            {
                ShowPersistentErrorWindow(_gamepadService.LastErrorMessage ??
                                          "Controller hotkey unavailable due to SDL2 initialization failure.");
            }
        }
    }

    private void RestoreGamepadBinding()
    {
        if (_hotkeySettings.ControllerButton.HasValue)
        {
            _gamepadBindingDescription = BuildControllerBindingText();
            _gamepadService?.BindFromSettings(
                _hotkeySettings.ControllerInstanceId ?? -1,
                (SDL2.SDL.SDL_GameControllerButton)_hotkeySettings.ControllerButton.Value,
                _hotkeySettings.ControllerName);
            UpdateControllerHotkeyText();
            UpdateControllerHotkeyStatus("Controller hotkey ready.");
        }
        else
        {
            UpdateControllerHotkeyStatus("Controller hotkey idle.");
        }
    }


    private async Task ApplyHotkeySelectionAsync(Key key, KeyModifiers modifiers)
    {
        if (IsModifierKey(key))
        {
            UpdateHotkeyText("Modifier-only keys not supported.");
            return;
        }

        _hotkey = key;
        _hotkeyModifiers = modifiers;
        _hotkeySettings = _hotkeySettings.WithLocal(key.ToString(), modifiers.ToString());
        SettingsStore.Save(_hotkeySettings);

        string? preferredTrigger = FormatGlobalTrigger(key, modifiers);
        if (_globalHotkeyPortal == null)
        {
            UpdateHotkeyStatus("Wayland portal unavailable. Trying X11 fallback...");
            if (!TryBindX11Hotkey(key, modifiers))
            {
                UpdateHotkeyStatus("Global hotkey unavailable. This program must be in focus for the keyboard hotkey to work.");
            }
            UpdateHotkeyText();
            return;
        }

        if (preferredTrigger == null)
        {
            _globalHotkeyBound = false;
            _globalHotkeyDescription = null;
            UpdateHotkeyStatus("That key isn't supported for global hotkeys. This program must now be in focus for the keyboard hotkey to work.");
            UpdateHotkeyText();
            return;
        }

        UpdateHotkeyStatus("Waiting for global hotkey approval...");
        var result = await _globalHotkeyPortal.RebindAsync("Force checkpoint", preferredTrigger, string.Empty);
        if (result.Success)
        {
            _globalHotkeyBound = true;
            _globalHotkeyDescription = result.TriggerDescription ?? preferredTrigger;
            _hotkeySettings = _hotkeySettings.WithGlobal(preferredTrigger, _globalHotkeyDescription);
            SettingsStore.Save(_hotkeySettings);
            _x11HotkeyBound = false;
            _x11HotkeyDescription = null;
            ClearX11Hotkey();
            UpdateHotkeyStatus("Global hotkey ready.");
        }
        else
        {
            _globalHotkeyBound = false;
            _globalHotkeyDescription = null;
            if (result.Message == "Global shortcut setup cancelled.")
            {
                UpdateHotkeyStatus("Global hotkey setup cancelled.");
            }
            else if (TryBindX11Hotkey(key, modifiers))
            {
                UpdateHotkeyStatus("Using X11 global hotkey fallback.");
            }
            else
            {
                UpdateHotkeyStatus(result.Message ?? "Global hotkey setup failed. Using focused hotkey.");
            }
        }

        UpdateHotkeyText();
    }

    private async Task ClearGlobalHotkeyAsync()
    {
        if (_globalHotkeyPortal != null)
        {
            await _globalHotkeyPortal.CloseSessionAsync();
        }
    }

    private void ClearX11Hotkey()
    {
        _x11HotkeyListener?.Dispose();
        _x11HotkeyListener = null;
    }

    private void UpdateHotkeyStatus(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (HotkeyStatusText != null)
            {
                HotkeyStatusText.Text = message;
            }
        });
    }

    private void UpdateControllerHotkeyStatus(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ControllerHotkeyStatusText != null)
            {
                ControllerHotkeyStatusText.Text = message;
            }
        });
    }

    private void UpdateControllerHotkeyText()
    {
        if (ControllerHotkeyText == null)
        {
            return;
        }

        ControllerHotkeyText.Text = string.IsNullOrWhiteSpace(_gamepadBindingDescription)
            ? "None"
            : _gamepadBindingDescription;
    }

    private string BuildControllerBindingText()
    {
        if (_hotkeySettings.ControllerButton.HasValue)
        {
            var button = (SDL2.SDL.SDL_GameControllerButton)_hotkeySettings.ControllerButton.Value;
            return GetControllerButtonLabel(button);
        }

        return "None";
    }

    private static string? FormatGlobalTrigger(Key key, KeyModifiers modifiers)
    {
        if (!TryMapKeyToKeysym(key, out string? keysym))
        {
            return null;
        }
        if (keysym == null)
        {
            return null;
        }

        var parts = new List<string>(4);
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("CTRL");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("SHIFT");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("ALT");
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("LOGO");
        }

        parts.Add(keysym);
        return string.Join('+', parts);
    }

    private void TryBindX11FromSettings()
    {
        if (_hotkey.HasValue)
        {
            if (TryBindX11Hotkey(_hotkey.Value, _hotkeyModifiers))
            {
                UpdateHotkeyStatus("Using X11 global hotkey fallback.");
            }
            else
            {
                UpdateHotkeyStatus("Global hotkey unavailable. This program must be in focus for the keyboard hotkey to work.");
            }
        }
        else
        {
            UpdateHotkeyStatus("Global hotkey unavailable. This program must be in focus for the keyboard hotkey to work.");
        }
    }

    private bool TryBindX11Hotkey(Key key, KeyModifiers modifiers)
    {
        bool isWayland = IsWaylandSession();
        bool isX11 = IsX11Session();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            UpdateHotkeyStatus("X11 not available (DISPLAY not set).");
            if (isX11 && !_x11DisplayWarningShown && !_hotkeySettings.SuppressX11DisplayWarning)
            {
                _x11DisplayWarningShown = true;
                ShowWarningWindow("X11 global hotkeys are unavailable because DISPLAY is not set. " +
                                  "If you are on Wayland, the portal hotkey is recommended.",
                    settings => settings.WithSuppressX11DisplayWarning(true));
            }
            _x11HotkeyBound = false;
            _x11HotkeyDescription = null;
            return false;
        }

        if (!TryMapKeyToKeysym(key, out string? keysym) || keysym == null)
        {
            _x11HotkeyBound = false;
            _x11HotkeyDescription = null;
            return false;
        }

        if (_x11HotkeyListener == null)
        {
            _x11HotkeyListener = new X11HotkeyListener();
            _x11HotkeyListener.Activated += () =>
            {
                Dispatcher.UIThread.Post(() => ForceCheckpointButton_OnClick(this, new RoutedEventArgs()));
            };
        }

        bool bound;
        try
        {
            bound = _x11HotkeyListener.Bind(
                keysym,
                modifiers.HasFlag(KeyModifiers.Control),
                modifiers.HasFlag(KeyModifiers.Shift),
                modifiers.HasFlag(KeyModifiers.Alt),
                modifiers.HasFlag(KeyModifiers.Meta));
        }
        catch (DllNotFoundException)
        {
            UpdateHotkeyStatus("X11 not available (libX11 missing).");
            if (isX11 && !_x11LibWarningShown && !_hotkeySettings.SuppressX11LibWarning)
            {
                _x11LibWarningShown = true;
                ShowWarningWindow("X11 global hotkeys are unavailable because libX11 is missing. " +
                                  "Install the X11 libraries or use the Wayland portal hotkey.",
                    settings => settings.WithSuppressX11LibWarning(true));
            }
            _x11HotkeyBound = false;
            _x11HotkeyDescription = null;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            UpdateHotkeyStatus("X11 not available (libX11 symbols missing).");
            if (isX11 && !_x11SymbolWarningShown && !_hotkeySettings.SuppressX11SymbolWarning)
            {
                _x11SymbolWarningShown = true;
                ShowWarningWindow("X11 global hotkeys are unavailable because libX11 is missing required symbols. " +
                                  "Install or update libX11, or use the Wayland portal hotkey.",
                    settings => settings.WithSuppressX11SymbolWarning(true));
            }
            _x11HotkeyBound = false;
            _x11HotkeyDescription = null;
            return false;
        }

        _x11HotkeyBound = bound;
        if (bound)
        {
            _globalHotkeyBound = false;
            _globalHotkeyDescription = null;
        }
        _x11HotkeyDescription = bound ? FormatHotkey(key, modifiers) : null;
        UpdateHotkeyText();
        return bound;
    }

    private static bool IsWaylandSession()
    {
        string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (!string.IsNullOrWhiteSpace(sessionType) &&
            sessionType.Equals("wayland", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    private static bool IsX11Session()
    {
        string? sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (!string.IsNullOrWhiteSpace(sessionType) &&
            sessionType.Equals("x11", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) && !IsWaylandSession();
    }

    private static bool TryMapKeyToKeysym(Key key, out string? keysym)
    {
        keysym = key switch
        {
            Key.A => "a",
            Key.B => "b",
            Key.C => "c",
            Key.D => "d",
            Key.E => "e",
            Key.F => "f",
            Key.G => "g",
            Key.H => "h",
            Key.I => "i",
            Key.J => "j",
            Key.K => "k",
            Key.L => "l",
            Key.M => "m",
            Key.N => "n",
            Key.O => "o",
            Key.P => "p",
            Key.Q => "q",
            Key.R => "r",
            Key.S => "s",
            Key.T => "t",
            Key.U => "u",
            Key.V => "v",
            Key.W => "w",
            Key.X => "x",
            Key.Y => "y",
            Key.Z => "z",
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            Key.NumPad0 => "KP_0",
            Key.NumPad1 => "KP_1",
            Key.NumPad2 => "KP_2",
            Key.NumPad3 => "KP_3",
            Key.NumPad4 => "KP_4",
            Key.NumPad5 => "KP_5",
            Key.NumPad6 => "KP_6",
            Key.NumPad7 => "KP_7",
            Key.NumPad8 => "KP_8",
            Key.NumPad9 => "KP_9",
            Key.Enter => "Return",
            Key.Tab => "Tab",
            Key.Space => "space",
            Key.Back => "BackSpace",
            Key.Escape => "Escape",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "Page_Up",
            Key.PageDown => "Page_Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.F1 => "F1",
            Key.F2 => "F2",
            Key.F3 => "F3",
            Key.F4 => "F4",
            Key.F5 => "F5",
            Key.F6 => "F6",
            Key.F7 => "F7",
            Key.F8 => "F8",
            Key.F9 => "F9",
            Key.F10 => "F10",
            Key.F11 => "F11",
            Key.F12 => "F12",
            Key.OemMinus => "minus",
            Key.OemPlus => "equal",
            Key.OemComma => "comma",
            Key.OemPeriod => "period",
            Key.OemQuestion => "slash",
            Key.OemSemicolon => "semicolon",
            Key.OemQuotes => "apostrophe",
            Key.OemOpenBrackets => "bracketleft",
            Key.OemCloseBrackets => "bracketright",
            Key.OemPipe => "backslash",
            Key.OemTilde => "grave",
            _ => null
        };

        return keysym != null;
    }

    private async void CheckForUpdateButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version == null)
        {
            ShowErrorWindow("Could not determine the current version.");
            return;
        }

        Version currentVersion = new(version.Major, version.Minor, version.Build);
        string latestReleaseUrl = "https://api.github.com/repos/Jestzer/Force.Halo.Checkpoints/releases/latest";

        using HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("Force.Halo.Checkpoints",
                $"{version.Major}.{version.Minor}.{version.Build}"));
        client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            string jsonString = await client.GetStringAsync(latestReleaseUrl);
            using JsonDocument doc = JsonDocument.Parse(jsonString);
            JsonElement root = doc.RootElement;
            string latestVersionString = root.GetProperty("tag_name").GetString()!.TrimStart('v');
            Version latestVersion = new(latestVersionString);

            if (currentVersion.CompareTo(latestVersion) < 0)
            {
                // Find the Linux asset URL.
                string? assetUrl = null;
                if (root.TryGetProperty("assets", out JsonElement assets))
                {
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        string? name = asset.GetProperty("name").GetString();
                        if (name != null && name.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                                         && name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                        {
                            assetUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }

                if (assetUrl == null)
                {
                    ShowErrorWindow($"A new version (V{latestVersionString}) is available, but no Linux build was found in the release assets.");
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    UpdateWindow updateWindow = new();
                    updateWindow.UpdateTextBlock.Text =
                        $"A new version (V{latestVersionString}) is available!\nYou are currently on V{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}.";

                    if (VisualRoot is Window window)
                    {
                        await updateWindow.ShowDialog(window);
                    }
                    else
                    {
                        updateWindow.Show();
                    }

                    if (updateWindow.Result == "download")
                    {
                        await DownloadUpdateAsync(assetUrl, latestVersionString, false);
                    }
                    else if (updateWindow.Result == "replace")
                    {
                        await DownloadUpdateAsync(assetUrl, latestVersionString, true);
                    }
                });
            }
            else
            {
                ShowErrorWindow("You are using the latest release available.");
            }
        }
        catch (JsonException ex)
        {
            ShowErrorWindow($"Failed to parse the update response: {ex.Message}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            ShowErrorWindow($"HTTP error 403: GitHub is saying you're sending too many requests. Try again later.\n\n{ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            ShowErrorWindow($"HTTP error while checking for updates: {ex.Message}");
        }
        catch (Exception ex)
        {
            ShowErrorWindow($"Failed to check for updates. Make sure you're connected to the internet.\n\n{ex.Message}");
        }
    }

    private async Task DownloadUpdateAsync(string assetUrl, string versionString, bool replaceAndRestart)
    {
        using HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.Add(
            new System.Net.Http.Headers.ProductInfoHeaderValue("Force.Halo.Checkpoints", "1.0.0"));

        try
        {
            byte[] data = await client.GetByteArrayAsync(assetUrl);
            string fileName = $"Force-Halo-Checkpoints-V{versionString}-Linux.tar.gz";

            if (!replaceAndRestart)
            {
                string downloadsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloadsPath);
                string savePath = Path.Combine(downloadsPath, fileName);
                await File.WriteAllBytesAsync(savePath, data);
                ShowErrorWindow($"Downloaded to:\n{savePath}");
                return;
            }

            // Download & Replace flow.
            string tempDir = Path.Combine(Path.GetTempPath(), "ForceHaloCheckpointsUpdate");
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
            Directory.CreateDirectory(tempDir);

            string tarPath = Path.Combine(tempDir, fileName);
            await File.WriteAllBytesAsync(tarPath, data);

            // Extract the tar.gz.
            var extractProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "tar",
                Arguments = $"-xzf \"{tarPath}\" -C \"{tempDir}\"",
                UseShellExecute = false,
                RedirectStandardError = true
            });

            if (extractProcess != null)
            {
                await extractProcess.WaitForExitAsync();
                if (extractProcess.ExitCode != 0)
                {
                    string error = await extractProcess.StandardError.ReadToEndAsync();
                    ShowErrorWindow($"Failed to extract the update archive:\n{error}");
                    return;
                }
            }

            // Find the new binary in the extracted files.
            string? currentExePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExePath))
            {
                ShowErrorWindow("Could not determine the current executable path.");
                return;
            }

            string currentExeName = Path.GetFileName(currentExePath);
            string[] candidates = Directory.GetFiles(tempDir, currentExeName, SearchOption.AllDirectories);

            if (candidates.Length == 0)
            {
                // Try finding any executable file in the extracted directory.
                candidates = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
                // Filter to files without extensions or known binary names.
                var filtered = new List<string>();
                foreach (string candidate in candidates)
                {
                    if (!candidate.EndsWith(".tar.gz") && !candidate.EndsWith(".md"))
                    {
                        filtered.Add(candidate);
                    }
                }
                candidates = filtered.ToArray();
            }

            if (candidates.Length == 0)
            {
                ShowErrorWindow("Could not find the new binary in the extracted archive.");
                return;
            }

            string newBinary = candidates[0];

            // Copy new binary over current binary.
            File.Copy(newBinary, currentExePath, overwrite: true);

            // Make it executable.
            var chmodProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{currentExePath}\"",
                UseShellExecute = false
            });
            chmodProcess?.WaitForExit();

            // Launch the new binary and close the current app.
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExePath,
                UseShellExecute = false
            });

            // Close the current application.
            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception ex)
        {
            ShowErrorWindow($"Failed to download the update:\n{ex.Message}");
        }
    }

    private void DetachProcess(int pid)
    {
        _ = ptrace(PtraceDetach, pid, IntPtr.Zero, new IntPtr(SigCont));
    }

    private void SetControllerHotkeyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        InitializeGamepadHotkey();
        if (_gamepadService?.LastErrorMessage != null)
        {
            ShowPersistentErrorWindow(_gamepadService.LastErrorMessage);
            UpdateControllerHotkeyStatus(_gamepadService.LastErrorMessage);
            return;
        }
        _gamepadService?.BeginCapture();
    }

    private void ClearControllerHotkeyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _gamepadBindingDescription = null;
        _hotkeySettings = _hotkeySettings.WithControllerBinding(null, null, null);
        SettingsStore.Save(_hotkeySettings);
        _gamepadService?.ClearBinding();
        UpdateControllerHotkeyText();
        UpdateControllerHotkeyStatus("Controller hotkey cleared.");
    }

    private static string GetControllerButtonLabel(SDL2.SDL.SDL_GameControllerButton button)
    {
        return button switch
        {
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A => "A button",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B => "B button",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X => "X button",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y => "Y button",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK => "Back button",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_GUIDE => "Guide button",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START => "Start button",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSTICK => "Left stick press",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSTICK => "Right stick press",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER => "Left bumper",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER => "Right bumper",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP => "D-pad up",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN => "D-pad down",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT => "D-pad left",
            SDL2.SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT => "D-pad right",
            _ => "Controller button"
        };
    }
}

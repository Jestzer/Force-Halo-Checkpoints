using System.Diagnostics;
using System.Media;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Force.Halo.Checkpoints
{
    public partial class MainWindow : Window
    {
        public string rawHotKeyString = string.Empty;
        public string gameSelected = string.Empty;
        public string friendlyGameName = string.Empty;
        private readonly Thread? inputThread;
        private readonly Thread? antiCheatCheckThread;
        private ushort controllerButtonSelected = 0;
        public string controllerTriggerSelected = string.Empty;
        volatile bool isControllerButtonSelected = false;
        volatile bool isRecordControllerInputDone = true;
        volatile bool isButtonCoolDownHappening = false;
        volatile bool isProgramClosing = false;
        private volatile bool isErrorWindowOpen = false;
        int failureCount = 0;
        bool isUsingWindowsStoreMCC = false;

        // I believe this it being set halfway depressed.
        const byte triggerThreshold = 128;

        // P/Invoke declarations for the hotkey hook, opening the process, reading/writing memory & and XInput.
        [DllImport("xinput1_4.dll")]
        public static extern int XInputGetState(int dwUserIndex, ref XInputState pState);

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesWritten);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, int cb, out int lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll")]
        public static extern uint GetModuleBaseName(IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName, int nSize);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using Process curProcess = Process.GetCurrentProcess();

            // Check if MainModule is not null before using it.
            ProcessModule? curModule = curProcess.MainModule ?? throw new InvalidOperationException("Main module could not be found.");

            // Now it's safe to use curModule.ModuleName, since we've checked if it's null.
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                GetModuleHandle(curModule.ModuleName), 0);
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN || nCode >= 0 && wParam == (IntPtr)WM_SYSKEYDOWN)
            {
                KBDLLHOOKSTRUCT kbStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (_instance != null && kbStruct.vkCode == _instance._currentHotkey)
                {
                    // Handle the key press
                    _instance.OnHotKeyPressed();
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        public static void InstallHook()
        {
            _hookID = SetHook(_proc);
        }

        public static void UninstallHook()
        {
            UnhookWindowsHookEx(_hookID);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XInputState
        {
            public int dwPacketNumber;
            public XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]

        public struct XInputGamepad
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
        }

        public const int XINPUT_GAMEPAD_DPAD_UP = 0x0001;
        public const int XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
        public const int XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
        public const int XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
        public const int XINPUT_GAMEPAD_START = 0x0010;
        public const int XINPUT_GAMEPAD_BACK = 0x0020;
        public const int XINPUT_GAMEPAD_LEFT_THUMB = 0x0040;
        public const int XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
        public const int XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
        public const int XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
        public const int XINPUT_GAMEPAD_A = 0x1000;
        public const int XINPUT_GAMEPAD_B = 0x2000;
        public const int XINPUT_GAMEPAD_X = 0x4000;
        public const int XINPUT_GAMEPAD_Y = 0x8000;

        public MainWindow()
        {
            if (!Properties.Settings.Default.IsDisclaimerAgreed)
            {
                DisclaimerWindow();
            }
            if (Properties.Settings.Default.IsDisclaimerAgreed)
            {
                InitializeComponent();
                _instance = this;

                // For printing the version number.
                DataContext = this;

                InstallHook();

                if (Properties.Settings.Default.HotKeyPreference != string.Empty)
                {
                    rawHotKeyString = Properties.Settings.Default.HotKeyPreference;
                }

                if (Properties.Settings.Default.LastGameSelected != string.Empty)
                {
                    gameSelected = Properties.Settings.Default.LastGameSelected;
                    GameSelectedTextBlock.Text = Properties.Settings.Default.LastGameSelectedLabel;
                    friendlyGameName = Properties.Settings.Default.LastGameFriendlyName;
                    StatusTextBlock.Text = "Status: Awaiting input";
                }

                if (Properties.Settings.Default.IsControllerButtonSelectedPreference == true)
                {
                    isControllerButtonSelected = true;
                    isRecordControllerInputDone = true;
                    controllerButtonSelected = Properties.Settings.Default.ControllerButtonPreference;
                    controllerTriggerSelected = Properties.Settings.Default.ControllerTriggerPreference;
                    ControllerButtonBindingTextBlock.Text = Properties.Settings.Default.ControllerButtonPreferenceString;
                }

                // Start some threads in the background.
                inputThread = new Thread(CheckControllerInput);
                inputThread.IsBackground = true;
                inputThread.Start();

                antiCheatCheckThread = new Thread(CheckForAntiCheatRunning);
                antiCheatCheckThread.IsBackground = true;
                antiCheatCheckThread.Start();
            }
        }
        private static void DisclaimerWindow()
        {
            DisclaimerWindow disclaimerWindow = new();
            disclaimerWindow.ShowDialog();
        }
        private void CheckForAntiCheatRunning()
        {
            while (isProgramClosing == false)
            {
                if (gameSelected != "Halo 2 Vista" && gameSelected != "Halo Custom Edition" && gameSelected != "Halo CE OG")
                {
                    string processName = "EasyAntiCheat";
                    int processID = GetProcessIdByName(processName);

                    if (processID != -1)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ForceCheckpointButton.Content = "Easy Anti-Cheat is running in the MCC!";
                            ForceCheckpointButton.IsEnabled = false;
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ForceCheckpointButton.Content = "Force Checkpoint";
                            ForceCheckpointButton.IsEnabled = true;
                        });
                    }
                }
                else if (gameSelected == "Halo Custom Edition" || gameSelected == "Halo CE OG")
                {
                    Dispatcher.Invoke(() =>
                    {
                        ForceCheckpointButton.Content = "Force Checkpoint";
                        ForceCheckpointButton.IsEnabled = true;
                    });
                }
                else
                {
                    string processName = "halo2";
                    int processID = GetProcessIdByName(processName);

                    if (processID != -1)
                    {
                        const int PROCESS_WM_READ = 0x0010;
                        const int PROCESS_VM_OPERATION = 0x0008;
                        string dllName = "xlive.dll";
                        int offset = 0x4E68FF;
                        IntPtr processHandle = IntPtr.Zero;
                        try
                        {
                            processHandle = OpenProcess(PROCESS_WM_READ | PROCESS_VM_OPERATION, false, processID);
                            IntPtr dllBaseAddress = GetModuleBaseAddress(processID, dllName);

                            if (dllBaseAddress == IntPtr.Zero)
                            {
                                ShowErrorWindow($"Failed to find the base address of {dllName}.");
                                return;
                            }

                            IntPtr addressToCheck = IntPtr.Add(dllBaseAddress, offset);
                            byte[] buffer = new byte[1];

                            bool result = ReadProcessMemory(processHandle, addressToCheck, buffer, buffer.Length, out int bytesRead);

                            if (result && bytesRead == buffer.Length)
                            {
                                byte valueRead = buffer[0];

                                if (valueRead != 0)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        ForceCheckpointButton.Content = "Anti-Cheat is running in Silent Cartographer!\r\n                          (Halo 2: Vista)";
                                        ForceCheckpointButton.IsEnabled = false;
                                    });
                                }
                                else
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        ForceCheckpointButton.Content = "Force Checkpoint";
                                        ForceCheckpointButton.IsEnabled = true;
                                    });
                                }
                            }
                            else
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    ShowErrorWindow("Failed to read memory.");
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            failureCount++;

                            if (failureCount < 3)
                            {
                                // Give Halo 2 Vista in all its old, terribleness, 3 seconds to breathe.
                                Thread.Sleep(1000);
                            }
                            else
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    ShowErrorWindow("There's an issue with your copy of Halo 2: Vista. Here's the full error message: " + ex.Message);
                                    gameSelected = string.Empty;
                                    StatusTextBlock.Text = "Status: Issue with H2V.";
                                });
                            }
                        }
                        finally
                        {
                            if (processHandle != IntPtr.Zero)
                            {
                                CloseHandle(processHandle);
                            }
                        }
                    }
                    else
                    {
                        failureCount = 0;

                        Dispatcher.Invoke(() =>
                        {
                            ForceCheckpointButton.Content = "Force Checkpoint";
                            ForceCheckpointButton.IsEnabled = true;
                        });
                    }
                }
                Thread.Sleep(2000);
            }
        }

        private async void CheckControllerInput()
        {
            try
            {
                while (true)
                {
                    if (!isButtonCoolDownHappening)
                    {
                        if (isControllerButtonSelected && isRecordControllerInputDone)
                        {
                            XInputState state = new();
                            int result = XInputGetState(0, ref state); // 0 is the first controller.

                            if (result == 0) // Controller is connected.
                            {
                                if ((state.Gamepad.wButtons & controllerButtonSelected) != 0)
                                {
                                    _ = Dispatcher.Invoke(() =>
                                    {
                                        ForceCheckpointButton_Click(this, new RoutedEventArgs());
                                        return Task.CompletedTask;
                                    });
                                }
                                // Triggers need to be handled separately.
                                else if (controllerTriggerSelected == "Left Trigger")
                                {
                                    if (state.Gamepad.bLeftTrigger > triggerThreshold)
                                    {
                                        Dispatcher.Invoke(() =>
                                        {
                                            ForceCheckpointButton_Click(this, new RoutedEventArgs());
                                        });
                                    }
                                }
                                else if (controllerTriggerSelected == "Right Trigger")
                                {
                                    if (state.Gamepad.bRightTrigger > triggerThreshold)
                                    {
                                        Dispatcher.Invoke(() =>
                                        {
                                            ForceCheckpointButton_Click(this, new RoutedEventArgs());
                                        });
                                    }
                                }
                            }
                            // Sleep to supposedly prevent high CPU usage.
                            Thread.Sleep(100);
                        }
                    }
                    // Prevent the user from triggering a checkpoint while selecting their controller button binding. Wait 2 seconds.
                    else
                    {
                        Dispatcher.Invoke(() => // Gotta use these to play around with the UI.
                        {
                            RecordControllerInputButton.Content = "Please wait 2 seconds.";
                            RecordControllerInputButton.IsEnabled = false;
                        });

                        await Task.Delay(2000);
                        isButtonCoolDownHappening = false;

                        Dispatcher.Invoke(() =>
                        {
                            RecordControllerInputButton.Content = "Start Recording Input";
                            RecordControllerInputButton.IsEnabled = true;
                        });
                    }
                }
            }
            catch (Exception)
            {
                // Prevent unhandled exceptions from crashing the process since this is async void.
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (Properties.Settings.Default.IsDisclaimerAgreed)
            {
                isProgramClosing = true;

                if (KeyBindingTextBox.Text != string.Empty)
                {
                    Properties.Settings.Default.HotKeyPreference = rawHotKeyString;
                }

                if (gameSelected != string.Empty)
                {
                    Properties.Settings.Default.LastGameSelected = gameSelected;
                    Properties.Settings.Default.LastGameFriendlyName = friendlyGameName;
                    Properties.Settings.Default.LastGameSelectedLabel = GameSelectedTextBlock.Text;
                }

                if (isControllerButtonSelected)
                {
                    Properties.Settings.Default.IsControllerButtonSelectedPreference = true;
                    Properties.Settings.Default.ControllerButtonPreference = controllerButtonSelected;
                    Properties.Settings.Default.ControllerTriggerPreference = controllerTriggerSelected;
                    Properties.Settings.Default.ControllerButtonPreferenceString = ControllerButtonBindingTextBlock.Text;
                }

                Properties.Settings.Default.Save();

                if (_hookID != IntPtr.Zero)
                {
                    UninstallHook();
                }
                _instance = null;
                base.OnClosed(e);
            }
        }

        private void ShowErrorWindow(string errorMessage)
        {
            isErrorWindowOpen = true;
            ErrorWindow errorWindow = new ErrorWindow();
            errorWindow.ErrorTextBlock.Text = errorMessage;
            errorWindow.Closed += ErrorWindow_Closed;
            errorWindow.Owner = this;
            SystemSounds.Exclamation.Play();
            StatusTextBlock.Text = "Status: " + errorMessage;
            errorWindow.ShowDialog();
        }

        private void ErrorWindow_Closed(object? sender, EventArgs e)
        {
            isErrorWindowOpen = false;

            // Unsubscribe from the Closed event.
            if (sender is ErrorWindow errorWindow)
            {
                errorWindow.Closed -= ErrorWindow_Closed;
            }
        }

        private void ShowUpdateWindow(string errorMessage, string customTitle)
        {
            isErrorWindowOpen = true;
            ErrorWindow errorWindow = new ErrorWindow();
            errorWindow.ErrorTextBlock.Text = errorMessage;
            errorWindow.Owner = this;
            errorWindow.Title = customTitle;
            SystemSounds.Exclamation.Play();
            errorWindow.ShowDialog();
            isErrorWindowOpen = false;
        }

        public static string PackageVersion
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                if (assembly != null)
                {
                    var version = assembly.GetName().Version;
                    if (version != null)
                    {
                        return version.ToString();
                    }
                }
                return "Error getting version number.";
            }
        }

        private async void CheckforUpdateButton_Click(object sender, EventArgs e)
        {
            Version currentVersion = new(PackageVersion);

            // GitHub API URL for the latest release.
            string latestReleaseUrl = "https://api.github.com/repos/Jestzer/Force.Halo.Checkpoints/releases/latest";

            // Use HttpClient to fetch the latest release data.
            using HttpClient client = new();

            // GitHub API requires a user-agent. I'm adding the extra headers to reduce HTTP error 403s.
            client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Force.Halo.Checkpoints", PackageVersion));
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                try
                {
                    // Make the latest release a JSON string.
                    string jsonString = await client.GetStringAsync(latestReleaseUrl);

                    // Parse the JSON to get the tag_name (version number).
                    using JsonDocument doc = JsonDocument.Parse(jsonString);
                    JsonElement root = doc.RootElement;
                    string latestVersionString = root.GetProperty("tag_name").GetString()!;

                    // Remove 'v' prefix if present in the tag name.
                    latestVersionString = latestVersionString.TrimStart('v');

                    // Parse the version string.
                    Version latestVersion = new(latestVersionString);

                    // Compare the current version with the latest version.
                    if (currentVersion.CompareTo(latestVersion) < 0)
                    {
                        // A newer version is available!
                        ErrorWindow errorWindow = new();
                        errorWindow.Owner = this;
                        errorWindow.ErrorTextBlock.Text = "";
                        errorWindow.URLTextBlock.IsEnabled = true;
                        errorWindow.URLTextBlock.Visibility = Visibility.Visible;
                        errorWindow.Title = "Check for updates";
                        errorWindow.ShowDialog();
                        errorWindow.URLTextBlock.IsEnabled = false;
                    }
                    else
                    {
                        ShowUpdateWindow("You are using the latest release available.", "Check for updates");
                    }
                }
                catch (JsonException ex)
                {
                    ShowUpdateWindow("The Json code in this program didn't work. Here's the automatic error message it made: \"" + ex.Message + "\"", "Check for updates");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    ShowUpdateWindow("HTTP error 403: GitHub is saying you're sending them too many requests, so... slow down, I guess? " +
                        "Here's the automatic error message: \"" + ex.Message + "\"", "Check for updates");
                }
                catch (HttpRequestException ex)
                {
                    ShowUpdateWindow("HTTP error. Here's the automatic error message: \"" + ex.Message + "\"", "Check for updates");
                }
            }
            catch (Exception ex)
            {
                ShowUpdateWindow("Oh dear, it looks this program had a hard time making the needed connection to GitHub. Make sure you're connected to the internet " +
                    "and your lousy firewall/VPN isn't blocking the connection. Here's the automated error message: \"" + ex.Message + "\"", "Check for updates");
            }
        }

        private string GetRawHotKeyString()
        {
            return rawHotKeyString;
        }

        [GeneratedRegex("(?<!^)([A-Z])")]
        private static partial Regex SpacesForNonFirstUppercaseLetters();

        [GeneratedRegex("D(\\d)")]
        private static partial Regex RemoveLetterDFromNumber();

        [GeneratedRegex("Num Pad")]
        private static partial Regex NumPadToNumberPad();

        private void RegisterCurrentHotkey(string hotKeyString)
        {
            string hotkeyName = hotKeyString.ToUpper();

            // Convert the key name to a Key enumeration, so it can actually be used.
            try
            {
                Key key = (Key)Enum.Parse(typeof(Key), hotkeyName, true);
                _currentHotkey = (uint)KeyInterop.VirtualKeyFromKey(key);

                if (hotkeyName == "RETURN")
                {
                    KeyBindingTextBox.Text = string.Empty;
                    rawHotKeyString = string.Empty;
                    ShowErrorWindow($"You may not use Enter/Return as your hotkey.");
                    UnregisterCurrentHotkey();
                    return;
                }

                KeyBindingTextBox.Text = hotKeyString;
                KeyBindingTextBox.Text = SpacesForNonFirstUppercaseLetters().Replace(KeyBindingTextBox.Text, " $1");
                KeyBindingTextBox.Text = RemoveLetterDFromNumber().Replace(KeyBindingTextBox.Text, "$1");
                KeyBindingTextBox.Text = NumPadToNumberPad().Replace(KeyBindingTextBox.Text, "Number Pad ");

                KeyBindingTextBox.Text = KeyBindingTextBox.Text switch
                {
                    "Oem1" => "Semicolon",
                    "Oem3" => "Backtick",
                    "Oem Open Brackets" => "Open Bracket",
                    "Oem5" => "Backslash",
                    "Oem6" => "Closed Bracket",
                    "Oem Question" => "Slash",
                    "Oem Backslash" => "Backslash",
                    "Oem Plus" => "Equals",
                    "Oem Minus" => "Hyphen",
                    "Oem Quotes" => "Single Quote",
                    "Oem Comma" => "Comma",
                    "Oem Period" => "Period",
                    "Back" => "Backspace",
                    "Capital" => "Caps Lock",
                    "L Win" => "Left Windows Key",
                    "R Win" => "Right Windows Key",
                    "Apps" => "Menu Key",
                    "Scroll" => "Scroll Lock",
                    _ => KeyBindingTextBox.Text
                };

            }
            catch (Exception ex)
            {
                KeyBindingTextBox.Text = string.Empty;
                rawHotKeyString = string.Empty;
                ShowErrorWindow($"Oh dear, you've ran into an error. Here's the automatic message: " + ex.Message);
                UnregisterCurrentHotkey();
                return;
            }

            // Set up the hook if it's somehow not already set.
            if (_hookID == IntPtr.Zero)
            {
                InstallHook();
            }
        }
        private void UnregisterCurrentHotkey()
        {
            _currentHotkey = 0;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _ = PresentationSource.FromVisual(this) as HwndSource;

            if (Properties.Settings.Default.HotKeyPreference != string.Empty)
            {
                RegisterCurrentHotkey(GetRawHotKeyString());
            }
        }

        private void OnHotKeyPressed()
        {
            ForceCheckpointButton_Click(this, new RoutedEventArgs());
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void ForceCheckpointWithEnterKey(object sender, KeyEventArgs e)
        {
            if (ForceCheckpointButton.IsEnabled)
            {
                if (e.Key == Key.Enter)
                {
                    ForceCheckpointButton_Click(sender, e);
                }
            }
        }

        private void HaloCEButton_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = $"Game selected: Halo: Combat Evolved";
            gameSelected = "Halo CE";
            friendlyGameName = "Halo: Combat Evolved";
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void Halo2Button_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = "Game selected: Halo 2";
            gameSelected = "Halo 2";
            friendlyGameName = gameSelected;
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void Halo3Button_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = "Game selected: Halo 3";
            gameSelected = "Halo 3";
            friendlyGameName = gameSelected;
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void Halo3ODSTButton_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = "Game selected: Halo 3: ODST";
            gameSelected = "Halo 3 ODST";
            friendlyGameName = "Halo 3: ODST";
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void HaloReachButton_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = "Game selected: Halo: Reach";
            gameSelected = "Halo Reach";
            friendlyGameName = "Halo: Reach";
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void Halo4Button_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = "Game selected: Halo 4";
            gameSelected = "Halo 4";
            friendlyGameName = gameSelected;
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void HaloCEOGButton_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = "Game selected: Halo: Combat Evolved (not MCC)";
            gameSelected = "Halo CE OG";
            friendlyGameName = "The original Halo: Combat Evolved for PC";
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void HaloCustomEditionButton_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = "Game selected: Halo: Custom Edition";
            gameSelected = "Halo Custom Edition";
            friendlyGameName = "Halo: Custom Edition";
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void Halo2VistaButton_Click(object sender, RoutedEventArgs e)
        {
            GameSelectedTextBlock.Text = "Game selected: Halo 2: Vista";
            gameSelected = "Halo 2 Vista";
            friendlyGameName = "Halo 2: Vista";
            StatusTextBlock.Text = "Status: Awaiting input";
        }

        private void CheckIfGameIsRunning(string gameSelected, out bool gameIsRunning)
        {
            gameIsRunning = true;

            try
            {
                string processName = string.Empty;

                if (gameSelected == "Halo CE OG")
                {
                    processName = "halo";
                }
                else if (gameSelected == "Halo Custom Edition")
                {
                    processName = "haloce";
                }
                else if (gameSelected == "Halo 2 Vista")
                {
                    processName = "halo2";
                }
                else
                {
                    processName = "MCC-Win64-Shipping";
                    isUsingWindowsStoreMCC = false;
                }

                int processID = GetProcessIdByName(processName);

                if (processID == -1 && processName == "MCC-Win64-Shipping")
                {
                    processName = "MCCWinStore-Win64-Shipping";
                    processID = GetProcessIdByName(processName);
                    isUsingWindowsStoreMCC = true;

                    if (processID == -1)
                    {
                        ShowErrorWindow("Halo: The Master Chief Collection is not running.");
                        gameIsRunning = false;
                        return;
                    }
                }
                else if (processID == -1)
                {
                    ShowErrorWindow($"{friendlyGameName} is not running.");
                    gameIsRunning = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                ShowErrorWindow(ex.Message);
            }
        }
        private void CheckIfMCCGameIsRunning(string gameSelected, string dllName, int offset, string expectedValue, out bool mccGameIsRunning)
        {
            mccGameIsRunning = true;
            string processName = string.Empty;
            IntPtr processHandle = IntPtr.Zero;
            try
            {
                const int PROCESS_WM_READ = 0x0010;
                const int PROCESS_VM_OPERATION = 0x0008;
                if (!isUsingWindowsStoreMCC)
                {
                    processName = "MCC-Win64-Shipping";
                }
                else
                {
                    processName = "MCCWinStore-Win64-Shipping";
                }

                int processID = GetProcessIdByName(processName);

                processHandle = OpenProcess(PROCESS_WM_READ | PROCESS_VM_OPERATION, false, processID);
                IntPtr dllBaseAddress = GetModuleBaseAddress(processID, dllName);

                if (dllBaseAddress == IntPtr.Zero)
                {
                    ShowErrorWindow($"{friendlyGameName} is not running in the MCC.");
                    mccGameIsRunning = false;
                    return;
                }

                byte[] buffer = new byte[4];

                IntPtr addressToCheck = IntPtr.Add(dllBaseAddress, offset);
                if (gameSelected == "Halo 2" || gameSelected == "Halo 3" || gameSelected == "Halo 4")
                {
                    buffer = new byte[1];
                }
                else if (gameSelected == "Halo CE" || gameSelected == "Halo Reach")
                {
                    buffer = new byte[5];
                }
                else if (gameSelected == "Halo 3 ODST")
                {
                    // Use the default.
                }

                bool result = ReadProcessMemory(processHandle, addressToCheck, buffer, buffer.Length, out int bytesRead);

                if (result && bytesRead == buffer.Length)
                {
                    if (gameSelected == "Halo 2" || gameSelected == "Halo 3" || gameSelected == "Halo 4")
                    {
                        byte valueRead = buffer[0];

                        if (valueRead == 0)
                        {
                            mccGameIsRunning = false;
                            Dispatcher.Invoke(() =>
                            {
                                ShowErrorWindow($"{friendlyGameName} is not running in the MCC.");
                            });
                            return;
                        }
                    }
                    else
                    {
                        string valueRead = System.Text.Encoding.ASCII.GetString(buffer);

                        if (valueRead != expectedValue)
                        {
                            mccGameIsRunning = false;
                            Dispatcher.Invoke(() =>
                            {
                                ShowErrorWindow($"{friendlyGameName} is not running in the MCC.");
                            });
                            return;
                        }
                    }
                }
                else
                {
                    ShowErrorWindow("Failed to read memory for the game you selected.");
                    mccGameIsRunning = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                ShowErrorWindow($"The attempt to check {friendlyGameName} in The Master Chief Collection failed. " +
                    "This is the automatic error message that was produced: " + ex.Message);
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    CloseHandle(processHandle);
                }
            }
        }

        private void ForceCheckpoint(string gameSelected, string dllName, int offset)
        {
            IntPtr processHandle = IntPtr.Zero;
            try
            {
                string processName = string.Empty;

                if (gameSelected == "Halo CE OG")
                {
                    processName = "halo";
                }
                else if (gameSelected == "Halo Custom Edition")
                {
                    processName = "haloce";
                }
                else if (gameSelected == "Halo 2 Vista")
                {
                    processName = "halo2";
                }
                else
                {
                    processName = "MCC-Win64-Shipping";
                }

                int processID = GetProcessIdByName(processName);

                if (processID == -1 && processName == "MCC-Win64-Shipping")
                {
                    processName = "MCCWinStore-Win64-Shipping";
                    processID = GetProcessIdByName(processName);
                    isUsingWindowsStoreMCC = true;

                    if (processID == -1)
                    {
                        ShowErrorWindow("Halo: The Master Chief Collection is not running.");
                        return;
                    }
                }
                else if (processID == -1)
                {
                    ShowErrorWindow($"{friendlyGameName} is not running.");
                    return;
                }

                const int PROCESS_WM_READ = 0x0010;
                const int PROCESS_WM_WRITE = 0x0020;
                const int PROCESS_VM_OPERATION = 0x0008;

                processHandle = OpenProcess(PROCESS_WM_READ | PROCESS_WM_WRITE | PROCESS_VM_OPERATION, false, processID);

                // Get the base address of the DLL in the process's memory space.
                IntPtr dllBaseAddress = GetModuleBaseAddress(processID, dllName);

                if (dllBaseAddress == IntPtr.Zero)
                {
                    ShowErrorWindow($"Failed to find the base address of {dllName}.");
                    return;
                }

                // Calculate the address to write to by adding the offset to the base address.
                IntPtr addressToWriteTo = IntPtr.Add(dllBaseAddress, offset);

                byte valueToWrite = 1;

                // Allocate a buffer with the value to write.
                byte[] buffer = [valueToWrite];

                // Write the value to the calculated address. Let us know what the result was.
                bool result = WriteProcessMemory(processHandle, addressToWriteTo, buffer, buffer.Length, out int bytesWritten);

                if (result && bytesWritten == buffer.Length)
                {
                    StatusTextBlock.Text = "Status: Checkpoint successfully forced!";
                }
                else
                {
                    StatusTextBlock.Text = "Status: Checkpoint unsuccessfully forced.";
                    ShowErrorWindow("Checkpoint unsuccessfully forced (Failed to write to process memory.)");
                    return;
                }
            }
            catch (Exception ex)
            {
                if (ex.Message == "Access is denied.")
                {
                    ShowErrorWindow($"A checkpoint cannot be forced because you are running {friendlyGameName} with administrative privileges, but not this program with " +
                        "administrative privileges. Either run this program with administrative privileges or run the game without administrative privileges.");
                }
                else
                {
                    ShowErrorWindow("The attempt to force a checkpoint failed. This is the automatic error message: " + ex.Message);
                    return;
                }
            }
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    CloseHandle(processHandle);
                }
            }
        }

        private void ForceCheckpointButton_Click(object sender, RoutedEventArgs e)
        {
            if (isErrorWindowOpen == false)
            {
                if (!isRecordingInput && !isRecordingControllerInput)
                {
                    if (ForceCheckpointButton.IsEnabled == true)
                    {
                        try
                        {
                            if (gameSelected == "Halo CE")
                            {
                                CheckIfGameIsRunning("MCC", out bool gameIsRunning);
                                if (gameIsRunning)
                                {
                                    CheckIfMCCGameIsRunning("Halo CE", "halo1.dll", 0x2DCF80B, "Halo1", out bool mccGameIsRunning);
                                    if (mccGameIsRunning)
                                    {
                                        ForceCheckpoint("Halo CE", "halo1.dll", 0x2B23707);
                                    }
                                }
                            }
                            else if (gameSelected == "Halo 2")
                            {
                                CheckIfGameIsRunning("MCC", out bool gameIsRunning);
                                if (gameIsRunning)
                                {
                                    CheckIfMCCGameIsRunning("Halo 2", "mss64dsp.flt", 0x24690, "1", out bool mccGameIsRunning);
                                    if (mccGameIsRunning)
                                    {
                                        ForceCheckpoint("Halo 2", "halo2.dll", 0xE70D7E);
                                    }
                                }
                            }
                            else if (gameSelected == "Halo 3")
                            {
                                CheckIfGameIsRunning("MCC", out bool gameIsRunning);
                                if (gameIsRunning)
                                {
                                    CheckIfMCCGameIsRunning("Halo 3", "halo3.dll", 0x46BCD0C, "1", out bool mccGameIsRunning);
                                    if (mccGameIsRunning)
                                    {
                                        ForceCheckpoint("Halo 3", "halo3.dll", 0x20B96AC);
                                    }
                                }
                            }
                            else if (gameSelected == "Halo 4")
                            {
                                CheckIfGameIsRunning("MCC", out bool gameIsRunning);
                                if (gameIsRunning)
                                {
                                    CheckIfMCCGameIsRunning("Halo 4", "halo4.dll", 0xE3B005, "1", out bool mccGameIsRunning);
                                    if (mccGameIsRunning)
                                    {
                                        ForceCheckpoint("Halo 4", "halo4.dll", 0x293DEAF);
                                    }
                                }
                            }
                            else if (gameSelected == "Halo Reach")
                            {
                                CheckIfGameIsRunning("MCC", out bool gameIsRunning);
                                if (gameIsRunning)
                                {
                                    CheckIfMCCGameIsRunning("Halo Reach", "haloreach.dll", 0xAFBC30, "reach", out bool mccGameIsRunning);
                                    if (mccGameIsRunning)
                                    {
                                        ForceCheckpoint("Halo Reach", "haloreach.dll", 0x263EABE);
                                    }
                                }
                            }
                            else if (gameSelected == "Halo 3 ODST")
                            {
                                CheckIfGameIsRunning("MCC", out bool gameIsRunning);
                                if (gameIsRunning)
                                {
                                    CheckIfMCCGameIsRunning("Halo 3 ODST", "halo3odst.dll", 0x2174F43, "ODST", out bool mccGameIsRunning);
                                    if (mccGameIsRunning)
                                    {
                                        ForceCheckpoint("Halo 3 ODST", "halo3odst.dll", 0x20FF6BC);
                                    }
                                }
                            }
                            else if (gameSelected == "Halo CE OG")
                            {
                                ForceCheckpoint("Halo CE OG", "halo.exe", 0x31973F);
                            }
                            else if (gameSelected == "Halo Custom Edition")
                            {
                                ForceCheckpoint("Halo Custom Edition", "haloce.exe", 0x2B47CF);
                            }
                            else if (gameSelected == "Halo 2 Vista")
                            {
                                ForceCheckpoint("Halo 2 Vista", "halo2.exe", 0x482250);
                            }
                            else
                            {
                                StatusTextBlock.Text = "Status: Please select a game first!";
                                ShowErrorWindow("Select a game first.");
                            }
                        }
                        catch (Exception ex)
                        {
                            ShowErrorWindow("Something went wrong in the function for the button forcing a checkpoint. " +
                                "Here's the automatic error message: " + ex.Message);
                        }
                    }
                    else
                    {
                        if (gameSelected == "Halo 2 Vista")
                        {
                            ShowErrorWindow("Anti-Cheat is running!");
                        }
                        else
                        {
                            ShowErrorWindow("Easy Anti-Cheat is running!");
                        }
                    }
                }
            }
        }

        private bool isRecordingInput = false;
        private uint _currentHotkey;
        private static MainWindow? _instance;

        private void RecordInputButton_Click(object sender, RoutedEventArgs e)
        {
            // Prevent accidental checkpoint trigger.
            UnregisterCurrentHotkey();

            // Toggle the recording state.
            isRecordingInput = !isRecordingInput;

            if (isRecordingInput)
            {
                KeyBindingTextBox.Text = string.Empty;
                rawHotKeyString = string.Empty;

                // Start recording.
                KeyDown += OnKeyDownHandler;
                RecordInputButton.Content = "Stop Recording Input";
            }
            else
            {
                // Stop recording.
                KeyDown -= OnKeyDownHandler;
                RecordInputButton.Content = "Start Recording Input";
            }
        }

        private void OnKeyDownHandler(object sender, KeyEventArgs e)
        {
            // Find out what the key being pressed is.
            Key key = (e.Key == Key.System ? e.SystemKey : e.Key);

            // Append the key pressed to the TextBox.
            rawHotKeyString += key.ToString();

            // Stop recording after the key is pressed.
            isRecordingInput = false;
            KeyDown -= OnKeyDownHandler;

            // Unregister the previous hotkey and register the new one.
            UnregisterCurrentHotkey();
            RegisterCurrentHotkey(GetRawHotKeyString());

            RecordInputButton.Content = "Start Recording Input";
        }

        private static int GetProcessIdByName(string processName)
        {
            // Get all processes with the specified name.
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length > 0)
                {
                    // Return the PID of the first process found.
                    return processes[0].Id;
                }
                else
                {
                    // No process found with the specified name.
                    return -1;
                }
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        private static IntPtr GetModuleBaseAddress(int processID, string moduleName)
        {
            IntPtr moduleBaseAddress = IntPtr.Zero;
            IntPtr[] moduleHandles = new IntPtr[1024];

            using Process process = Process.GetProcessById(processID);
            IntPtr processHandle = process.Handle;

            if (EnumProcessModulesEx(processHandle, moduleHandles, IntPtr.Size * moduleHandles.Length, out int bytesNeeded, 0x03))
            {
                int numOfModules = bytesNeeded / IntPtr.Size;
                for (int i = 0; i < numOfModules; i++)
                {
                    StringBuilder sbModuleName = new StringBuilder(255);
                    if (GetModuleBaseName(processHandle, moduleHandles[i], sbModuleName, sbModuleName.Capacity) > 0)
                    {
                        if (sbModuleName.ToString().Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                        {
                            moduleBaseAddress = moduleHandles[i];
                            break;
                        }
                    }
                }
            }
            return moduleBaseAddress;
        }

        private readonly Dictionary<ushort, string> controllerButtonMappings = new Dictionary<ushort, string>
            {
                { XINPUT_GAMEPAD_DPAD_UP, "DPad Up" },
                { XINPUT_GAMEPAD_DPAD_DOWN, "DPad Down" },
                { XINPUT_GAMEPAD_DPAD_LEFT, "DPad Left" },
                { XINPUT_GAMEPAD_DPAD_RIGHT, "DPad Right" },
                { XINPUT_GAMEPAD_START, "Start" },
                { XINPUT_GAMEPAD_BACK, "Back" },
                { XINPUT_GAMEPAD_LEFT_THUMB, "Left Thumbstick" },
                { XINPUT_GAMEPAD_RIGHT_THUMB, "Right Thumbstick" },
                { XINPUT_GAMEPAD_LEFT_SHOULDER, "Left Bumper" },
                { XINPUT_GAMEPAD_RIGHT_SHOULDER, "Right Bumper" },
                { XINPUT_GAMEPAD_A, "A" },
                { XINPUT_GAMEPAD_B, "B" },
                { XINPUT_GAMEPAD_X, "X" },
                { XINPUT_GAMEPAD_Y, "Y" }
            };

        private CancellationTokenSource? cancellationTokenSource;
        private bool isRecordingControllerInput = false;
        private Thread? controllerInputCheckThread;

        private void RecordControllerInputButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isRecordingControllerInput)
            {
                RecordControllerInputButton.Content = "Stop Recording Input";
                isRecordingControllerInput = true;

                cancellationTokenSource = new CancellationTokenSource();
                controllerInputCheckThread = new Thread(() => RecordControllerInput(cancellationTokenSource.Token))
                {
                    IsBackground = true
                };
                controllerInputCheckThread.Start();
            }
            else
            {
                // Signal the thread to stop.
                cancellationTokenSource?.Cancel();
                isRecordingControllerInput = false;
                RecordControllerInputButton.Content = "Start Recording Input";
            }
        }

        private void RecordControllerInput(CancellationToken token)
        {
            isControllerButtonSelected = false;
            isRecordControllerInputDone = false;
            isButtonCoolDownHappening = false;

            while (!isControllerButtonSelected && !token.IsCancellationRequested)
            {
                XInputState state = new();
                int result = XInputGetState(0, ref state);

                if (result == 0)
                {
                    foreach (var buttonMapping in controllerButtonMappings)
                    {
                        if ((state.Gamepad.wButtons & buttonMapping.Key) != 0)
                        {
                            ushort buttonValue = buttonMapping.Key;
                            string buttonName = buttonMapping.Value;
                            Dispatcher.Invoke(() =>
                            {
                                controllerButtonSelected = buttonValue;
                                ControllerButtonBindingTextBlock.Text = buttonName;
                                controllerTriggerSelected = string.Empty;
                                isControllerButtonSelected = true;
                                isButtonCoolDownHappening = true;
                            });
                            break;
                        }
                    }

                    // Triggers aren't in the dictionary because they aren't boolean values.
                    if (state.Gamepad.bLeftTrigger > triggerThreshold)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            controllerButtonSelected = 0;
                            controllerTriggerSelected = "Left Trigger";
                            ControllerButtonBindingTextBlock.Text = controllerTriggerSelected;
                            isControllerButtonSelected = true;
                            isButtonCoolDownHappening = true;
                        });
                        break;
                    }

                    if (state.Gamepad.bRightTrigger > triggerThreshold)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            controllerButtonSelected = 0;
                            controllerTriggerSelected = "Right Trigger";
                            ControllerButtonBindingTextBlock.Text = controllerTriggerSelected;
                            isControllerButtonSelected = true;
                            isButtonCoolDownHappening = true;
                        });
                        break;
                    }

                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowErrorWindow("Your controller is either disconnected or unsupported.");
                        RecordControllerInputButton.Content = "Start Recording Input";
                    });
                    cancellationTokenSource?.Cancel();
                    isRecordingControllerInput = false;
                    break;
                }
            }
            Dispatcher.Invoke(() =>
            {
                // If a button was selected, reset the recording flag.
                if (isControllerButtonSelected)
                {
                    isRecordingControllerInput = false;
                    RecordControllerInputButton.Content = "Please wait 2 seconds.";
                }
                else if (controllerButtonSelected != 0)
                {
                    isControllerButtonSelected = true;
                }
                isRecordControllerInputDone = true;
            });
        }
    }
}

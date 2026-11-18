using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Device.Location;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FakeGPS.UI
{
    public class LatLong
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Name { get; set; }

        public LatLong(double lat, double lon, string name = "")
        {
            Latitude = lat;
            Longitude = lon;
            Name = name;
        }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Name))
                return string.Format("{0}, {1}", Latitude.ToString("F6", CultureInfo.InvariantCulture), Longitude.ToString("F6", CultureInfo.InvariantCulture));
            return string.Format("{0} ({1}, {2})", Name, Latitude.ToString("F4", CultureInfo.InvariantCulture), Longitude.ToString("F4", CultureInfo.InvariantCulture));
        }
    }

    public static class FakeGPSCore
    {
        private const string LatitudeProperty = "SENSOR_PROPERTY_LATITUDE";
        private const string LongitudeProperty = "SENSOR_PROPERTY_LONGITUDE";

        public static string FindDeviceRegistryPath(out string instanceId)
        {
            instanceId = "ROOT\\SENSOR\\0000";
            string[] candidateBases = new string[]
            {
                @"SYSTEM\CurrentControlSet\Enum\ROOT\SENSOR",
                @"SYSTEM\CurrentControlSet\Enum\ROOT\UNKNOWN"
            };

            foreach (var basePath in candidateBases)
            {
                using (var baseKey = Registry.LocalMachine.OpenSubKey(basePath, false))
                {
                    if (baseKey != null)
                    {
                        foreach (var sub in baseKey.GetSubKeyNames())
                        {
                            string p = basePath + @"\" + sub + @"\Device Parameters\FakeGPS";
                            using (var k = Registry.LocalMachine.OpenSubKey(p, false))
                            {
                                if (k != null)
                                {
                                    instanceId = basePath.Substring(@"SYSTEM\CurrentControlSet\Enum\".Length) + @"\" + sub;
                                    return p;
                                }
                            }
                        }
                    }
                }
            }

            return @"SYSTEM\CurrentControlSet\Enum\ROOT\SENSOR\0000\Device Parameters\FakeGPS";
        }

        public static bool IsDriverActive()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-devices /instanceid \"ROOT\\SENSOR\\0000\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    return output.Contains("Status:                    Started") || output.Contains("Status:                    OK") || output.Contains("ROOT\\SENSOR\\0000");
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetCoordinates(double lat, double lon, out string instanceId)
        {
            string path = FindDeviceRegistryPath(out instanceId);
            using (var key = Registry.LocalMachine.OpenSubKey(path, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Ключ FakeGPS не найден в реестре. Убедитесь, что драйвер установлен.");
                }

                key.SetValue(LatitudeProperty, lat.ToString(CultureInfo.InvariantCulture));
                key.SetValue(LongitudeProperty, lon.ToString(CultureInfo.InvariantCulture));
            }

            RestartDevice(instanceId);
        }

        public static LatLong GetConfiguredCoordinates()
        {
            string instanceId;
            string path = FindDeviceRegistryPath(out instanceId);
            using (var key = Registry.LocalMachine.OpenSubKey(path, false))
            {
                if (key == null) return null;
                object latVal = key.GetValue(LatitudeProperty);
                object lonVal = key.GetValue(LongitudeProperty);
                if (latVal != null && lonVal != null)
                {
                    double lat, lon;
                    if (double.TryParse(latVal.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lat) &&
                        double.TryParse(lonVal.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out lon))
                    {
                        return new LatLong(lat, lon);
                    }
                }
            }
            return null;
        }

        public static LatLong QueryWindowsLocationApi()
        {
            using (var watcher = new GeoCoordinateWatcher(GeoPositionAccuracy.High))
            {
                watcher.TryStart(false, TimeSpan.FromMilliseconds(2000));
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(100);
                    if (!watcher.Position.Location.IsUnknown) break;
                }

                var loc = watcher.Position.Location;
                return new LatLong(loc.Latitude, loc.Longitude);
            }
        }

        public static bool RestartDevice(string instanceId = "ROOT\\SENSOR\\0000")
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = string.Format("/restart-device \"{0}\"", instanceId),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(8000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    public class AppLocationItem
    {
        public string DisplayName { get; set; }
        public string ExePath { get; set; }
        public string RegistryKeyName { get; set; }
        public bool IsAllowed { get; set; }
    }

    public static class PerAppLocationManager
    {
        private const string BaseConsentPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location\NonPackaged";

        public static List<AppLocationItem> GetApps()
        {
            var list = new List<AppLocationItem>();

            using (var baseKey = Registry.CurrentUser.OpenSubKey(BaseConsentPath, true))
            {
                if (baseKey == null) return list;

                foreach (var subName in baseKey.GetSubKeyNames())
                {
                    using (var sub = baseKey.OpenSubKey(subName, false))
                    {
                        if (sub != null)
                        {
                            object val = sub.GetValue("Value");
                            string valStr = val != null ? val.ToString() : "Allow";
                            bool allowed = string.Equals(valStr, "Allow", StringComparison.OrdinalIgnoreCase);

                            string decodedPath = subName.Replace('#', '\\');
                            string displayName = Path.GetFileName(decodedPath);
                            if (string.IsNullOrEmpty(displayName)) displayName = subName;

                            list.Add(new AppLocationItem
                            {
                                DisplayName = displayName,
                                ExePath = decodedPath,
                                RegistryKeyName = subName,
                                IsAllowed = allowed
                            });
                        }
                    }
                }
            }

            return list;
        }

        public static void SetAppPermission(string registryKeyName, bool allow)
        {
            string subPath = BaseConsentPath + @"\" + registryKeyName;
            using (var key = Registry.CurrentUser.OpenSubKey(subPath, true))
            {
                if (key != null)
                {
                    key.SetValue("Value", allow ? "Allow" : "Deny");
                }
            }
        }

        public static void AddAppByPath(string exePath, bool allow)
        {
            string encoded = exePath.Replace('\\', '#');
            string subPath = BaseConsentPath + @"\" + encoded;
            using (var key = Registry.CurrentUser.CreateSubKey(subPath))
            {
                if (key != null)
                {
                    key.SetValue("Value", allow ? "Allow" : "Deny");
                }
            }
        }
    }

    public static class IsolatedAppLauncher
    {
        public static Process LaunchWithSpoofing(string exePath, string arguments, string timeZoneId, string proxyUrl = "")
        {
            if (string.IsNullOrEmpty(timeZoneId))
                timeZoneId = "Europe/Amsterdam";

            var psi = new ProcessStartInfo();
            psi.FileName = exePath;

            // Build Chromium / Electron flags for zero-leak spoofing
            var extraArgs = new List<string>();
            extraArgs.Add(string.Format("--timezone=\"{0}\"", timeZoneId));
            extraArgs.Add("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");
            extraArgs.Add("--enforce-webrtc-ip-permission-check");
            extraArgs.Add("--disable-features=GeolocationBssid");

            if (!string.IsNullOrEmpty(proxyUrl))
            {
                extraArgs.Add(string.Format("--proxy-server=\"{0}\"", proxyUrl.Trim()));
            }

            string combinedArgs = string.Join(" ", extraArgs.ToArray());
            if (!string.IsNullOrEmpty(arguments))
            {
                combinedArgs = arguments + " " + combinedArgs;
            }

            psi.Arguments = combinedArgs;
            psi.UseShellExecute = false;

            // Set Environment Variables for V8, Node, WebView2, and C-Runtime
            psi.EnvironmentVariables["TZ"] = timeZoneId;
            psi.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = combinedArgs;

            // Ensure app is allowed in Windows Location Consent Store
            try
            {
                PerAppLocationManager.AddAppByPath(exePath, true);
            }
            catch { }

            return Process.Start(psi);
        }

        public static string FindBrowserPath()
        {
            string[] paths = new string[]
            {
                @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
            };

            foreach (var p in paths)
            {
                if (File.Exists(p)) return p;
            }
            return "";
        }
    }

    public static class AIKillSwitch
    {
        private static readonly string[] AIProcessNames = new string[]
        {
            "claude", "chatgpt", "perplexity", "comet"
        };

        private const string FirewallRuleName = "FakeGPS_AI_KillSwitch_Block";

        public static int TerminateAllAIProcesses()
        {
            int killed = 0;
            foreach (var name in AIProcessNames)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(name);
                    foreach (var p in procs)
                    {
                        try
                        {
                            p.Kill();
                            killed++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return killed;
        }

        public static bool IsFirewallBlockActive()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = string.Format("advfirewall firewall show rule name=\"{0}\"", FirewallRuleName),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    return output.Contains(FirewallRuleName);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetFirewallBlock(bool block)
        {
            try
            {
                RunNetsh(string.Format("advfirewall firewall delete rule name=\"{0}\"", FirewallRuleName));

                if (block)
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                    string[] pathsToBlock = new string[]
                    {
                        Path.Combine(localAppData, @"Programs\Claude\Claude.exe"),
                        Path.Combine(programFiles, @"ChatGPT\ChatGPT.exe"),
                        Path.Combine(localAppData, @"ChatGPT\ChatGPT.exe")
                    };

                    foreach (var path in pathsToBlock)
                    {
                        if (File.Exists(path))
                        {
                            RunNetsh(string.Format("advfirewall firewall add rule name=\"{0}\" dir=out action=block program=\"{1}\" enable=yes",
                                FirewallRuleName, path));
                        }
                    }

                    RunNetsh(string.Format("advfirewall firewall add rule name=\"{0}\" dir=out action=block protocol=TCP remoteport=443 description=\"FakeGPS Emergency Block\"",
                        FirewallRuleName + "_Domains"));
                }
            }
            catch { }
        }

        private static void RunNetsh(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var proc = Process.Start(psi))
            {
                proc.WaitForExit(4000);
            }
        }
    }

    public class MainForm : Form
    {
        private TabControl tabs;
        private TabPage tabDashboard;
        private TabPage tabPerApp;
        private TabPage tabLauncher;
        private TabPage tabKillSwitch;
        private TabPage tabAudit;

        // Dashboard controls
        private Label lblDriverStatus;
        private Label lblCurrentConfig;
        private Label lblApiLocation;
        private ComboBox cbPresets;
        private TextBox txtLat;
        private TextBox txtLon;
        private Button btnApply;
        private Button btnRefresh;
        private Button btnRestartDriver;
        private RichTextBox rtbLog;

        // PerApp controls
        private ListView lvApps;
        private Button btnScanApps;
        private Button btnAddCustomApp;
        private Button btnAllowAll;
        private Button btnDenyAll;

        // Launcher controls (Per-App Timezone & Shield)
        private ComboBox cbLauncherTz;
        private TextBox txtLauncherProxy;
        private TextBox txtCustomExe;
        private RichTextBox rtbLauncherLog;

        // KillSwitch controls
        private Label lblKillSwitchStatus;
        private Button btnEmergencyPanic;
        private CheckBox chkAutoKill;
        private CheckBox chkFirewallGuard;
        private RichTextBox rtbKillSwitchLog;
        private System.Windows.Forms.Timer guardTimer;

        // Audit controls
        private Label lblAuditGps;
        private Label lblAuditIp;
        private Label lblAuditCountry;
        private Label lblAuditVerdict;
        private Button btnRunAudit;

        public MainForm()
        {
            InitializeComponent();
            ApplyModernDarkTheme();
            RefreshDashboard();
            RefreshAppList();
            UpdateKillSwitchUI();
        }

        private void InitializeComponent()
        {
            this.Text = "FakeGPS Controller & AI Guard (Windows 11 Edition)";
            this.Size = new Size(860, 680);
            this.MinimumSize = new Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            tabs = new TabControl { Dock = DockStyle.Fill };
            tabDashboard = new TabPage { Text = "  🛰️ GPS Контроллер  " };
            tabPerApp = new TabPage { Text = "  🎯 Спуфинг по приложениям  " };
            tabLauncher = new TabPage { Text = "  🚀 Изолированный лаунчер (Timezone + GPS)  " };
            tabKillSwitch = new TabPage { Text = "  🛡️ AI Kill Switch (Claude / ChatGPT)  " };
            tabAudit = new TabPage { Text = "  🔍 Аудит утечек  " };

            tabs.TabPages.Add(tabDashboard);
            tabs.TabPages.Add(tabPerApp);
            tabs.TabPages.Add(tabLauncher);
            tabs.TabPages.Add(tabKillSwitch);
            tabs.TabPages.Add(tabAudit);

            BuildDashboardTab();
            BuildPerAppTab();
            BuildLauncherTab();
            BuildKillSwitchTab();
            BuildAuditTab();

            this.Controls.Add(tabs);

            guardTimer = new System.Windows.Forms.Timer();
            guardTimer.Interval = 4000;
            guardTimer.Tick += GuardTimer_Tick;
            guardTimer.Start();
        }

        private void ApplyModernDarkTheme()
        {
            Color darkBg = Color.FromArgb(24, 24, 27);
            Color textLight = Color.FromArgb(244, 244, 245);

            this.BackColor = darkBg;
            this.ForeColor = textLight;

            foreach (TabPage tab in tabs.TabPages)
            {
                tab.BackColor = darkBg;
                tab.ForeColor = textLight;
            }
        }

        private void BuildDashboardTab()
        {
            Panel pnlTop = new Panel { Dock = DockStyle.Top, Height = 140, Padding = new Padding(15) };
            
            lblDriverStatus = new Label { Location = new Point(15, 12), AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
            lblCurrentConfig = new Label { Location = new Point(15, 45), AutoSize = true };
            lblApiLocation = new Label { Location = new Point(15, 75), AutoSize = true };

            btnRefresh = new Button { Text = "🔄 Обновить статус", Location = new Point(620, 15), Size = new Size(180, 34) };
            btnRefresh.Click += (s, e) => RefreshDashboard();

            btnRestartDriver = new Button { Text = "⚡ Перезапуск драйвера", Location = new Point(620, 58), Size = new Size(180, 34) };
            btnRestartDriver.Click += (s, e) =>
            {
                Log("Перезапуск PnP узла драйвера FakeGPS...");
                bool ok = FakeGPSCore.RestartDevice();
                Log(ok ? "Драйвер успешно перезапущен!" : "Не удалось перезапустить драйвер через pnputil.");
                RefreshDashboard();
            };

            pnlTop.Controls.AddRange(new Control[] { lblDriverStatus, lblCurrentConfig, lblApiLocation, btnRefresh, btnRestartDriver });

            GroupBox gb = new GroupBox
            {
                Text = "Выбор координат (Нидерланды и мир)",
                Location = new Point(15, 150),
                Size = new Size(810, 160),
                ForeColor = Color.White
            };

            Label lblPresets = new Label { Text = "Быстрый пресет:", Location = new Point(20, 30), AutoSize = true };
            cbPresets = new ComboBox { Location = new Point(160, 27), Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
            
            var presets = new LatLong[]
            {
                new LatLong(52.379189, 4.899431, "🇳🇱 Нидерланды — Амстердам (Центр)"),
                new LatLong(51.924420, 4.477733, "🇳🇱 Нидерланды — Роттердам"),
                new LatLong(52.070498, 4.300699, "🇳🇱 Нидерланды — Гаага (The Hague)"),
                new LatLong(52.090737, 5.121420, "🇳🇱 Нидерланды — Утрехт"),
                new LatLong(51.441642, 5.469722, "🇳🇱 Нидерланды — Эйндховен"),
                new LatLong(51.507351, -0.127758, "🇬🇧 Великобритания — Лондон"),
                new LatLong(50.110922, 8.682127, "🇩🇪 Германия — Франкфурт"),
                new LatLong(40.712776, -74.005974, "🇺🇸 США — Нью-Йорк"),
                new LatLong(35.676192, 139.650311, "🇯🇵 Япония — Токио")
            };

            foreach (var p in presets) cbPresets.Items.Add(p);
            cbPresets.SelectedIndex = 0;
            cbPresets.SelectedIndexChanged += (s, e) =>
            {
                var sel = cbPresets.SelectedItem as LatLong;
                if (sel != null)
                {
                    txtLat.Text = sel.Latitude.ToString(CultureInfo.InvariantCulture);
                    txtLon.Text = sel.Longitude.ToString(CultureInfo.InvariantCulture);
                }
            };

            Label lblLat = new Label { Text = "Широта (Lat):", Location = new Point(20, 72), AutoSize = true };
            txtLat = new TextBox { Location = new Point(160, 69), Width = 170, Text = "52.379189" };

            Label lblLon = new Label { Text = "Долгота (Lon):", Location = new Point(350, 72), AutoSize = true };
            txtLon = new TextBox { Location = new Point(460, 69), Width = 170, Text = "4.899431" };

            btnApply = new Button
            {
                Text = "🚀 Применить геолокацию в системе",
                Location = new Point(160, 108),
                Size = new Size(300, 38),
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnApply.Click += (s, e) =>
            {
                try
                {
                    double lat = double.Parse(txtLat.Text.Trim(), CultureInfo.InvariantCulture);
                    double lon = double.Parse(txtLon.Text.Trim(), CultureInfo.InvariantCulture);

                    string instanceId;
                    FakeGPSCore.SetCoordinates(lat, lon, out instanceId);
                    Log(string.Format("Успешно записаны координаты: {0}, {1} (Устройство: {2})", lat, lon, instanceId));
                    Log("Драйвер перезапущен. Проверка Windows Location API...");
                    RefreshDashboard();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Ошибка ввода: " + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            gb.Controls.AddRange(new Control[] { lblPresets, cbPresets, lblLat, txtLat, lblLon, txtLon, btnApply });

            rtbLog = new RichTextBox
            {
                Location = new Point(15, 325),
                Size = new Size(810, 270),
                BackColor = Color.FromArgb(15, 15, 18),
                ForeColor = Color.FromArgb(161, 161, 170),
                Font = new Font("Consolas", 9f),
                ReadOnly = true
            };

            tabDashboard.Controls.AddRange(new Control[] { pnlTop, gb, rtbLog });
        }

        private void BuildPerAppTab()
        {
            Panel pnlHeader = new Panel { Dock = DockStyle.Top, Height = 90, Padding = new Padding(15) };
            Label lblInfo = new Label
            {
                Text = "Управление доступом к геолокации Windows для каждого приложения.\n" +
                       "Приложения с отметкой 'Разрешить' получают спуфленный GPS. Приложения с 'Запретить' не могут определить позицию.",
                AutoSize = true,
                Location = new Point(15, 10)
            };

            btnScanApps = new Button { Text = "🔄 Обновить список", Location = new Point(15, 50), Size = new Size(150, 30) };
            btnScanApps.Click += (s, e) => RefreshAppList();

            btnAddCustomApp = new Button { Text = "➕ Добавить приложение (.exe)", Location = new Point(180, 50), Size = new Size(210, 30) };
            btnAddCustomApp.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog { Filter = "Executable Files (*.exe)|*.exe|All files (*.*)|*.*" })
                {
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        PerAppLocationManager.AddAppByPath(ofd.FileName, true);
                        RefreshAppList();
                    }
                }
            };

            btnAllowAll = new Button { Text = "Разрешить всем", Location = new Point(410, 50), Size = new Size(140, 30) };
            btnAllowAll.Click += (s, e) => ToggleAllApps(true);

            btnDenyAll = new Button { Text = "Запретить всем", Location = new Point(560, 50), Size = new Size(140, 30) };
            btnDenyAll.Click += (s, e) => ToggleAllApps(false);

            pnlHeader.Controls.AddRange(new Control[] { lblInfo, btnScanApps, btnAddCustomApp, btnAllowAll, btnDenyAll });

            lvApps = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                BackColor = Color.FromArgb(15, 15, 18),
                ForeColor = Color.White
            };
            lvApps.Columns.Add("Доступ (FakeGPS)", 130);
            lvApps.Columns.Add("Приложение", 180);
            lvApps.Columns.Add("Полный путь", 480);

            lvApps.ItemChecked += (s, e) =>
            {
                if (e.Item.Tag is AppLocationItem)
                {
                    var item = (AppLocationItem)e.Item.Tag;
                    if (item.IsAllowed != e.Item.Checked)
                    {
                        item.IsAllowed = e.Item.Checked;
                        PerAppLocationManager.SetAppPermission(item.RegistryKeyName, item.IsAllowed);
                        e.Item.SubItems[0].Text = item.IsAllowed ? "✅ Разрешено" : "❌ Запрещено";
                    }
                }
            };

            tabPerApp.Controls.Add(lvApps);
            tabPerApp.Controls.Add(pnlHeader);
        }

        private void BuildLauncherTab()
        {
            Panel pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };

            Label lblTitle = new Label
            {
                Text = "Изолированный запуск приложений (Спуфинг Timezone + GPS без изменения системы)",
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold),
                Location = new Point(20, 15),
                AutoSize = true
            };

            Label lblDesc = new Label
            {
                Text = "Запускает процесс с индивидуальным часовым поясом (V8 / Chromium Intl.DateTimeFormat),\n" +
                       "передает спуфленный GPS из FakeGPS, блокирует WebRTC утечки и Wi-Fi триангуляцию.\n" +
                       "Системные часы и настройки Windows остаются нетронутыми!",
                Location = new Point(20, 48),
                AutoSize = true,
                ForeColor = Color.FromArgb(161, 161, 170)
            };

            GroupBox gbSettings = new GroupBox
            {
                Text = "Параметры изолированного окружения",
                Location = new Point(20, 115),
                Size = new Size(800, 110),
                ForeColor = Color.White
            };

            Label lblTz = new Label { Text = "Часовой пояс (Timezone):", Location = new Point(20, 30), AutoSize = true };
            cbLauncherTz = new ComboBox { Location = new Point(210, 27), Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
            cbLauncherTz.Items.Add("Europe/Amsterdam (Нидерланды, CET UTC+1)");
            cbLauncherTz.Items.Add("Europe/London (Великобритания, GMT UTC+0)");
            cbLauncherTz.Items.Add("Europe/Berlin (Германия, CET UTC+1)");
            cbLauncherTz.Items.Add("Europe/Paris (Франция, CET UTC+1)");
            cbLauncherTz.Items.Add("America/New_York (США Восток, EST UTC-5)");
            cbLauncherTz.Items.Add("Asia/Tokyo (Япония, JST UTC+9)");
            cbLauncherTz.SelectedIndex = 0;

            Label lblProxy = new Label { Text = "SOCKS5 / HTTP Proxy:", Location = new Point(20, 68), AutoSize = true };
            txtLauncherProxy = new TextBox { Location = new Point(210, 65), Width = 320, Text = "" };
            Label lblProxyHint = new Label { Text = "(Опционально. Пример: socks5://127.0.0.1:10808)", Location = new Point(540, 68), AutoSize = true, ForeColor = Color.Gray };

            gbSettings.Controls.AddRange(new Control[] { lblTz, cbLauncherTz, lblProxy, txtLauncherProxy, lblProxyHint });

            // Launch buttons group
            GroupBox gbActions = new GroupBox
            {
                Text = "Быстрый защищенный запуск",
                Location = new Point(20, 235),
                Size = new Size(800, 150),
                ForeColor = Color.White
            };

            Button btnLaunchChatGPT = new Button
            {
                Text = "🤖 Запустить ChatGPT (WebView2 / App)",
                Location = new Point(20, 30),
                Size = new Size(360, 42),
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            btnLaunchChatGPT.Click += (s, e) =>
            {
                string tz = GetSelectedTimezone();
                string proxy = txtLauncherProxy.Text.Trim();

                LogLauncher(string.Format("Запуск ChatGPT с часовым поясом '{0}'...", tz));
                // Try launching ChatGPT via ms-windows-store uri or direct executable
                string appxPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1111.2228.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
                if (File.Exists(appxPath))
                {
                    IsolatedAppLauncher.LaunchWithSpoofing(appxPath, "", tz, proxy);
                    LogLauncher("ChatGPT запущен в изолированном режиме с параметрами анти-детекта.");
                }
                else
                {
                    // Launch browser ChatGPT
                    string browser = IsolatedAppLauncher.FindBrowserPath();
                    if (!string.IsNullOrEmpty(browser))
                    {
                        IsolatedAppLauncher.LaunchWithSpoofing(browser, "https://chatgpt.com", tz, proxy);
                        LogLauncher("ChatGPT запущен в защищенном окне браузера: " + browser);
                    }
                    else
                    {
                        LogLauncher("Не удалось найти исполняемый файл ChatGPT или браузера.");
                    }
                }
            };

            Button btnLaunchClaude = new Button
            {
                Text = "🧠 Запустить Claude (Браузер с Amsterdam TZ + GPS)",
                Location = new Point(410, 30),
                Size = new Size(360, 42),
                BackColor = Color.FromArgb(139, 92, 246),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            btnLaunchClaude.Click += (s, e) =>
            {
                string tz = GetSelectedTimezone();
                string proxy = txtLauncherProxy.Text.Trim();
                string browser = IsolatedAppLauncher.FindBrowserPath();

                if (!string.IsNullOrEmpty(browser))
                {
                    string profileDir = Path.Combine(Path.GetTempPath(), "FakeGPS_Claude_Profile");
                    string args = string.Format("https://claude.ai --user-data-dir=\"{0}\"", profileDir);
                    IsolatedAppLauncher.LaunchWithSpoofing(browser, args, tz, proxy);
                    LogLauncher("Claude запущен в чистом профиле с часовым поясом " + tz + " и защитой WebRTC.");
                }
                else
                {
                    LogLauncher("Браузер (Brave / Chrome / Edge) не найден.");
                }
            };

            Label lblCustom = new Label { Text = "Свой .EXE:", Location = new Point(20, 95), AutoSize = true };
            txtCustomExe = new TextBox { Location = new Point(100, 92), Width = 370 };
            Button btnBrowseCustom = new Button { Text = "Обзор...", Location = new Point(480, 90), Size = new Size(90, 30) };
            btnBrowseCustom.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" })
                {
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        txtCustomExe.Text = ofd.FileName;
                    }
                }
            };

            Button btnLaunchCustom = new Button { Text = "🚀 Запустить изолированно", Location = new Point(585, 90), Size = new Size(185, 30) };
            btnLaunchCustom.Click += (s, e) =>
            {
                string path = txtCustomExe.Text.Trim();
                if (File.Exists(path))
                {
                    string tz = GetSelectedTimezone();
                    string proxy = txtLauncherProxy.Text.Trim();
                    IsolatedAppLauncher.LaunchWithSpoofing(path, "", tz, proxy);
                    LogLauncher(string.Format("Запущено: {0} [TZ={1}]", Path.GetFileName(path), tz));
                }
                else
                {
                    MessageBox.Show(this, "Укажите существующий путь к .exe файлу.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            gbActions.Controls.AddRange(new Control[]
            {
                btnLaunchChatGPT, btnLaunchClaude, lblCustom, txtCustomExe, btnBrowseCustom, btnLaunchCustom
            });

            rtbLauncherLog = new RichTextBox
            {
                Location = new Point(20, 395),
                Size = new Size(800, 200),
                BackColor = Color.FromArgb(15, 15, 18),
                ForeColor = Color.FromArgb(96, 165, 250),
                Font = new Font("Consolas", 9f),
                ReadOnly = true
            };

            pnl.Controls.AddRange(new Control[] { lblTitle, lblDesc, gbSettings, gbActions, rtbLauncherLog });
            tabLauncher.Controls.Add(pnl);
        }

        private string GetSelectedTimezone()
        {
            string txt = cbLauncherTz.SelectedItem.ToString();
            int idx = txt.IndexOf(' ');
            return (idx > 0) ? txt.Substring(0, idx) : "Europe/Amsterdam";
        }

        private void BuildKillSwitchTab()
        {
            Panel pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };

            lblKillSwitchStatus = new Label
            {
                Text = "Статус AI Guard: АКТИВЕН",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(16, 185, 129),
                Location = new Point(20, 20),
                AutoSize = true
            };

            Label lblDesc = new Label
            {
                Text = "Kill Switch мгновенно защищает ваши аккаунты Claude, ChatGPT и других нейросетей от бана.\n" +
                       "Если FakeGPS отключится или произойдет утечка, защита закроет процессы или заблокирует сеть.",
                Location = new Point(20, 55),
                AutoSize = true
            };

            btnEmergencyPanic = new Button
            {
                Text = "🚨 АВАРИЙНЫЙ KILL SWITCH\n(Немедленно завершить Claude, ChatGPT и включить Firewall Block)",
                Location = new Point(20, 110),
                Size = new Size(540, 60),
                BackColor = Color.FromArgb(239, 68, 68),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat
            };
            btnEmergencyPanic.Click += (s, e) =>
            {
                int killed = AIKillSwitch.TerminateAllAIProcesses();
                AIKillSwitch.SetFirewallBlock(true);
                LogKillSwitch(string.Format("🚨 ВЫЗВАН АВАРИЙНЫЙ KILL SWITCH! Завершено процессов нейросетей: {0}. Включена блокировка фаервола.", killed));
                UpdateKillSwitchUI();
            };

            Button btnUnlock = new Button
            {
                Text = "Снять блокировку Firewall",
                Location = new Point(575, 120),
                Size = new Size(185, 40)
            };
            btnUnlock.Click += (s, e) =>
            {
                AIKillSwitch.SetFirewallBlock(false);
                LogKillSwitch("Блокировка Firewall снята.");
                UpdateKillSwitchUI();
            };

            chkAutoKill = new CheckBox
            {
                Text = "Авто-ликвидация: Закрывать Claude и ChatGPT при падении драйвера FakeGPS",
                Location = new Point(25, 190),
                AutoSize = true,
                Checked = true
            };

            chkFirewallGuard = new CheckBox
            {
                Text = "Авто-изоляция: Активировать сетевую блокировку при обнаружении ошибки датчика",
                Location = new Point(25, 220),
                AutoSize = true,
                Checked = false
            };

            Label lblLogTitle = new Label { Text = "Журнал защиты нейросетей:", Location = new Point(20, 260), AutoSize = true };
            rtbKillSwitchLog = new RichTextBox
            {
                Location = new Point(20, 285),
                Size = new Size(740, 240),
                BackColor = Color.FromArgb(15, 15, 18),
                ForeColor = Color.FromArgb(52, 211, 153),
                Font = new Font("Consolas", 9f),
                ReadOnly = true
            };

            pnl.Controls.AddRange(new Control[]
            {
                lblKillSwitchStatus, lblDesc, btnEmergencyPanic, btnUnlock,
                chkAutoKill, chkFirewallGuard, lblLogTitle, rtbKillSwitchLog
            });

            tabKillSwitch.Controls.Add(pnl);
        }

        private void BuildAuditTab()
        {
            Panel pnl = new Panel { Dock = DockStyle.Fill, Padding = new Padding(25) };

            Label lblTitle = new Label
            {
                Text = "Диагностика полной маскировки: GPS vs Внешний IP vs Часовой пояс",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                Location = new Point(20, 20),
                AutoSize = true
            };

            lblAuditGps = new Label { Location = new Point(25, 70), AutoSize = true, Text = "1. GPS Сенсор Windows: Опрос..." };
            lblAuditIp = new Label { Location = new Point(25, 110), AutoSize = true, Text = "2. Публичный IP-адрес: Опрос..." };
            lblAuditCountry = new Label { Location = new Point(25, 150), AutoSize = true, Text = "3. Страна по IP: Опрос..." };
            lblAuditVerdict = new Label
            {
                Location = new Point(25, 200),
                AutoSize = true,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                Text = "Вердикт безопасности: Нажмите кнопку для проверки"
            };

            btnRunAudit = new Button
            {
                Text = "🔎 Запустить аудит маскировки",
                Location = new Point(25, 260),
                Size = new Size(260, 42),
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnRunAudit.Click += (s, e) => RunSecurityAudit();

            pnl.Controls.AddRange(new Control[]
            {
                lblTitle, lblAuditGps, lblAuditIp, lblAuditCountry, lblAuditVerdict, btnRunAudit
            });

            tabAudit.Controls.Add(pnl);
        }

        private void RefreshDashboard()
        {
            bool active = FakeGPSCore.IsDriverActive();
            lblDriverStatus.Text = active ? "🟢 Драйвер FakeGPS: АКТИВЕН (OK)" : "🔴 Драйвер FakeGPS: НЕ АКТИВЕН / ОШИБКА";
            lblDriverStatus.ForeColor = active ? Color.FromArgb(16, 185, 129) : Color.FromArgb(239, 68, 68);

            var cfg = FakeGPSCore.GetConfiguredCoordinates();
            if (cfg != null)
            {
                lblCurrentConfig.Text = string.Format("Конфигурация в реестре: Широта {0}, Долгота {1}",
                    cfg.Latitude.ToString("F6", CultureInfo.InvariantCulture),
                    cfg.Longitude.ToString("F6", CultureInfo.InvariantCulture));
            }
            else
            {
                lblCurrentConfig.Text = "Конфигурация в реестре: Не задана";
            }

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    var loc = FakeGPSCore.QueryWindowsLocationApi();
                    this.Invoke((MethodInvoker)delegate
                    {
                        lblApiLocation.Text = string.Format("Ответ Windows Location API: Широта {0}, Долгота {1}",
                            loc.Latitude.ToString("F6", CultureInfo.InvariantCulture),
                            loc.Longitude.ToString("F6", CultureInfo.InvariantCulture));
                    });
                }
                catch { }
            });
        }

        private void RefreshAppList()
        {
            lvApps.Items.Clear();
            var apps = PerAppLocationManager.GetApps();
            foreach (var a in apps)
            {
                var lvi = new ListViewItem(a.IsAllowed ? "✅ Разрешено" : "❌ Запрещено");
                lvi.Checked = a.IsAllowed;
                lvi.SubItems.Add(a.DisplayName);
                lvi.SubItems.Add(a.ExePath);
                lvi.Tag = a;
                lvApps.Items.Add(lvi);
            }
        }

        private void ToggleAllApps(bool allow)
        {
            foreach (ListViewItem item in lvApps.Items)
            {
                item.Checked = allow;
            }
        }

        private void UpdateKillSwitchUI()
        {
            bool blocked = AIKillSwitch.IsFirewallBlockActive();
            if (blocked)
            {
                lblKillSwitchStatus.Text = "Статус AI Guard: 🔴 СЕТЬ ЗАБЛОКИРОВАНА (FIREWALL KILL SWITCH)";
                lblKillSwitchStatus.ForeColor = Color.FromArgb(239, 68, 68);
            }
            else
            {
                lblKillSwitchStatus.Text = "Статус AI Guard: 🟢 АКТИВЕН (Мониторинг защиты)";
                lblKillSwitchStatus.ForeColor = Color.FromArgb(16, 185, 129);
            }
        }

        private void GuardTimer_Tick(object sender, EventArgs e)
        {
            bool active = FakeGPSCore.IsDriverActive();
            if (!active)
            {
                lblDriverStatus.Text = "🔴 Драйвер FakeGPS: НЕ АКТИВЕН / ОШИБКА";
                lblDriverStatus.ForeColor = Color.FromArgb(239, 68, 68);

                if (chkAutoKill.Checked)
                {
                    int killed = AIKillSwitch.TerminateAllAIProcesses();
                    if (killed > 0)
                    {
                        LogKillSwitch(string.Format("⚠️ ОБНАРУЖЕН СБОЙ FAKEGPS! Автоматически закрыто {0} процессов нейросетей во избежание детекта.", killed));
                    }
                }

                if (chkFirewallGuard.Checked && !AIKillSwitch.IsFirewallBlockActive())
                {
                    AIKillSwitch.SetFirewallBlock(true);
                    LogKillSwitch("⚠️ Включена аварийная сетевая блокировка Firewall!");
                    UpdateKillSwitchUI();
                }
            }
        }

        private void RunSecurityAudit()
        {
            btnRunAudit.Enabled = false;
            lblAuditVerdict.Text = "Идет проверка...";

            ThreadPool.QueueUserWorkItem(state =>
            {
                var loc = FakeGPSCore.QueryWindowsLocationApi();
                string publicIp = "Неизвестно";
                string country = "Неизвестно";

                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "FakeGPS-Audit");
                        string json = wc.DownloadString("http://ip-api.com/json");
                        var mIp = Regex.Match(json, "\"query\"\\s*:\\s*\"([^\"]+)\"");
                        var mCountry = Regex.Match(json, "\"country\"\\s*:\\s*\"([^\"]+)\"");
                        if (mIp.Success) publicIp = mIp.Groups[1].Value;
                        if (mCountry.Success) country = mCountry.Groups[1].Value;
                    }
                }
                catch { }

                this.Invoke((MethodInvoker)delegate
                {
                    lblAuditGps.Text = string.Format("1. GPS Сенсор Windows: Широта {0}, Долгота {1}",
                        loc.Latitude.ToString("F4", CultureInfo.InvariantCulture),
                        loc.Longitude.ToString("F4", CultureInfo.InvariantCulture));

                    lblAuditIp.Text = "2. Публичный IP-адрес: " + publicIp;
                    lblAuditCountry.Text = "3. Страна по IP: " + country;

                    bool gpsIsNetherlands = (loc.Latitude >= 50.7 && loc.Latitude <= 53.6 && loc.Longitude >= 3.3 && loc.Longitude <= 7.3);
                    bool ipIsNetherlands = country.IndexOf("Netherlands", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (gpsIsNetherlands && ipIsNetherlands)
                    {
                        lblAuditVerdict.Text = "✅ ВЕРДИКТ: ИДЕАЛЬНАЯ МАСКИРОВКА! И GPS, и IP указывают на Нидерланды. Нейросети не обнаружат подмену.";
                        lblAuditVerdict.ForeColor = Color.FromArgb(16, 185, 129);
                    }
                    else if (gpsIsNetherlands && !ipIsNetherlands)
                    {
                        lblAuditVerdict.Text = string.Format("⚠️ ВНИМАНИЕ: РАСХОЖДЕНИЕ ДАННЫХ! GPS указывает на Нидерланды, но ваш IP из '{0}'.\n" +
                            "Claude и ChatGPT могут обнаружить подмену по IP-адресу! Включите нидерландский VPN.", country);
                        lblAuditVerdict.ForeColor = Color.FromArgb(245, 158, 11);
                    }
                    else
                    {
                        lblAuditVerdict.Text = "❌ ВЕРДИКТ: Локация не замаскирована под Нидерланды.";
                        lblAuditVerdict.ForeColor = Color.FromArgb(239, 68, 68);
                    }

                    btnRunAudit.Enabled = true;
                });
            });
        }

        private void Log(string msg)
        {
            rtbLog.AppendText(string.Format("[{0}] {1}\n", DateTime.Now.ToString("HH:mm:ss"), msg));
            rtbLog.ScrollToCaret();
        }

        private void LogLauncher(string msg)
        {
            rtbLauncherLog.AppendText(string.Format("[{0}] {1}\n", DateTime.Now.ToString("HH:mm:ss"), msg));
            rtbLauncherLog.ScrollToCaret();
        }

        private void LogKillSwitch(string msg)
        {
            rtbKillSwitchLog.AppendText(string.Format("[{0}] {1}\n", DateTime.Now.ToString("HH:mm:ss"), msg));
            rtbKillSwitchLog.ScrollToCaret();
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}

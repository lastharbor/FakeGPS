using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Device.Location;
using System.Text;
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
        public string Timezone { get; set; }

        public LatLong(double lat, double lon, string name = "", string tz = "Europe/Amsterdam")
        {
            Latitude = lat;
            Longitude = lon;
            Name = name;
            Timezone = tz;
        }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Name))
                return string.Format("{0}, {1}", Latitude.ToString("F6", CultureInfo.InvariantCulture), Longitude.ToString("F6", CultureInfo.InvariantCulture));
            return Name;
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
                    return output.Contains("Started") || output.Contains("OK") || output.Contains("Запущено");
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

    public static class OpenMapGeocoder
    {
        public static List<LatLong> Search(string query)
        {
            var results = new List<LatLong>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string url = "https://nominatim.openstreetmap.org/search?format=json&limit=5&q=" + Uri.EscapeDataString(query.Trim());

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "FakeGPS-Win11-Controller/2.0";
            req.Timeout = 8000;

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string json = reader.ReadToEnd();
                var matches = Regex.Matches(json, "\"lat\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"lon\"\\s*:\\s*\"([^\"]+)\".*?\"display_name\"\\s*:\\s*\"([^\"]+)\"");
                foreach (Match m in matches)
                {
                    double lat, lon;
                    if (double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out lat) &&
                        double.TryParse(m.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out lon))
                    {
                        string name = Regex.Unescape(m.Groups[3].Value);
                        results.Add(new LatLong(lat, lon, name));
                    }
                }
            }

            return results;
        }
    }

    public class DiscoveredAIApp
    {
        public string Name { get; set; }
        public string Category { get; set; } // Desktop, UWP, CLI, Browser Tab
        public string ProcessName { get; set; }
        public string ExecutablePath { get; set; }
        public bool IsRunning { get; set; }
        public int Pid { get; set; }
        public bool SelectedForKillSwitch { get; set; }
    }

    public static class AIDiscoveryEngine
    {
        private static readonly string[] KnownAIKeywords = new string[]
        {
            "claude", "chatgpt", "openai", "anthropic", "perplexity", "comet",
            "cursor", "windsurf", "ollama", "lmstudio", "lm studio", "jan",
            "anythingllm", "chatbox", "cherry", "deepseek", "grok", "poe", "copilot"
        };

        private static readonly string[] BrowserProcessNames = new string[]
        {
            "brave", "chrome", "msedge", "firefox", "opera", "vivaldi", "arc", "comet"
        };

        public static List<DiscoveredAIApp> DiscoverAll()
        {
            var found = new List<DiscoveredAIApp>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Scan running processes
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string pname = p.ProcessName.ToLowerInvariant();
                        string title = "";
                        try { title = p.MainWindowTitle ?? ""; } catch { }
                        string path = "";
                        try { path = p.MainModule != null ? p.MainModule.FileName : ""; } catch { }

                        bool isKnownProcess = false;
                        foreach (var kw in KnownAIKeywords)
                        {
                            if (pname.Contains(kw.Replace(" ", "")))
                            {
                                isKnownProcess = true;
                                break;
                            }
                        }

                        bool isBrowserWithAI = false;
                        if (!isKnownProcess && !string.IsNullOrEmpty(title))
                        {
                            foreach (var b in BrowserProcessNames)
                            {
                                if (pname == b)
                                {
                                    string lowerTitle = title.ToLowerInvariant();
                                    foreach (var kw in KnownAIKeywords)
                                    {
                                        if (lowerTitle.Contains(kw) || lowerTitle.Contains("gemini"))
                                        {
                                            isBrowserWithAI = true;
                                            break;
                                        }
                                    }
                                    break;
                                }
                            }
                        }

                        if (isKnownProcess || isBrowserWithAI)
                        {
                            string key = !string.IsNullOrEmpty(path) ? path + ":" + pname : pname + ":" + p.Id;
                            if (isKnownProcess && seenPaths.Contains(key)) continue;
                            seenPaths.Add(key);

                            found.Add(new DiscoveredAIApp
                            {
                                Name = isBrowserWithAI ? string.Format("{0} ({1})", p.ProcessName, title) : p.ProcessName,
                                Category = isBrowserWithAI ? "Browser AI Tab" : (path.Contains("WindowsApps") ? "UWP / Store App" : "Desktop / CLI"),
                                ProcessName = p.ProcessName,
                                ExecutablePath = path,
                                IsRunning = true,
                                Pid = p.Id,
                                SelectedForKillSwitch = true
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 2. Scan installed paths in %LocalAppData%\Programs and %UserProfile%\.local\bin
            var scanDirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin")
            };

            foreach (var dir in scanDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var exe in Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories))
                    {
                        string fname = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                        foreach (var kw in KnownAIKeywords)
                        {
                            if (fname.Contains(kw.Replace(" ", "")) && !fname.Contains("unins") && !fname.Contains("update"))
                            {
                                string key = exe + ":" + fname;
                                if (!seenPaths.Contains(key))
                                {
                                    seenPaths.Add(key);
                                    found.Add(new DiscoveredAIApp
                                    {
                                        Name = Path.GetFileNameWithoutExtension(exe),
                                        Category = "Installed (Offline)",
                                        ProcessName = Path.GetFileNameWithoutExtension(exe),
                                        ExecutablePath = exe,
                                        IsRunning = false,
                                        Pid = 0,
                                        SelectedForKillSwitch = true
                                    });
                                }
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. Scan WindowsApps for OpenAI.ChatGPT / OpenAI.Codex / Claude
            string winApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(winApps))
            {
                try
                {
                    foreach (var folder in Directory.GetDirectories(winApps))
                    {
                        string folderName = Path.GetFileName(folder).ToLowerInvariant();
                        if (folderName.Contains("openai") || folderName.Contains("claude") || folderName.Contains("anthropic") || folderName.Contains("perplexity"))
                        {
                            string appExe = Path.Combine(folder, @"app\ChatGPT.exe");
                            if (!File.Exists(appExe))
                            {
                                var exes = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories);
                                if (exes.Length > 0) appExe = exes[0];
                            }
                            if (File.Exists(appExe))
                            {
                                string pname = Path.GetFileNameWithoutExtension(appExe);
                                string key = appExe + ":" + pname.ToLowerInvariant();
                                if (!seenPaths.Contains(key))
                                {
                                    seenPaths.Add(key);
                                    found.Add(new DiscoveredAIApp
                                    {
                                        Name = pname + " (Store)",
                                        Category = "UWP / Store App",
                                        ProcessName = pname,
                                        ExecutablePath = appExe,
                                        IsRunning = false,
                                        Pid = 0,
                                        SelectedForKillSwitch = true
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return found;
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
                if (key != null) key.SetValue("Value", allow ? "Allow" : "Deny");
            }
        }

        public static void AddAppByPath(string exePath, bool allow)
        {
            string encoded = exePath.Replace('\\', '#');
            string subPath = BaseConsentPath + @"\" + encoded;
            using (var key = Registry.CurrentUser.CreateSubKey(subPath))
            {
                if (key != null) key.SetValue("Value", allow ? "Allow" : "Deny");
            }
        }
    }

    public static class IsolatedAppLauncher
    {
        public static Process LaunchWithSpoofing(string exePath, string arguments, string timeZoneId, string proxyUrl = "")
        {
            if (string.IsNullOrEmpty(timeZoneId)) timeZoneId = "Europe/Amsterdam";

            var psi = new ProcessStartInfo();
            psi.FileName = exePath;

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
            psi.EnvironmentVariables["TZ"] = timeZoneId;
            psi.EnvironmentVariables["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = combinedArgs;

            try { PerAppLocationManager.AddAppByPath(exePath, true); } catch { }
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
        private const string FirewallRuleName = "FakeGPS_AI_KillSwitch_Block";

        public static int TerminateDiscoveredAI(List<DiscoveredAIApp> targets)
        {
            int killed = 0;
            var procNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in targets)
            {
                if (t.SelectedForKillSwitch && !string.IsNullOrEmpty(t.ProcessName))
                {
                    procNames.Add(t.ProcessName);
                }
            }

            foreach (var name in procNames)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); killed++; } catch { }
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
            catch { return false; }
        }

        public static void SetFirewallBlock(bool block, List<DiscoveredAIApp> targets)
        {
            try
            {
                RunNetsh(string.Format("advfirewall firewall delete rule name=\"{0}\"", FirewallRuleName));
                if (block && targets != null)
                {
                    var blockedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in targets)
                    {
                        if (t.SelectedForKillSwitch && !string.IsNullOrEmpty(t.ExecutablePath) && File.Exists(t.ExecutablePath))
                        {
                            blockedExes.Add(t.ExecutablePath);
                        }
                    }

                    foreach (var exe in blockedExes)
                    {
                        RunNetsh(string.Format("advfirewall firewall add rule name=\"{0}\" dir=out action=block program=\"{1}\" enable=yes",
                            FirewallRuleName, exe));
                    }
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
            using (var proc = Process.Start(psi)) { proc.WaitForExit(4000); }
        }
    }

    [ComVisible(true)]
    public class MapScriptBridge
    {
        private Action<double, double> onPick;
        public MapScriptBridge(Action<double, double> onPickCallback) { onPick = onPickCallback; }

        public void OnMapClick(double lat, double lon)
        {
            if (onPick != null) onPick(lat, lon);
        }
    }

    public class MainForm : Form
    {
        // Theme Palette
        private readonly Color BgMain = Color.FromArgb(17, 17, 21);
        private readonly Color BgSidebar = Color.FromArgb(24, 24, 31);
        private readonly Color BgCard = Color.FromArgb(30, 30, 38);
        private readonly Color BorderCard = Color.FromArgb(48, 48, 60);
        private readonly Color AccentEmerald = Color.FromArgb(16, 185, 129);
        private readonly Color AccentIndigo = Color.FromArgb(99, 102, 241);
        private readonly Color AccentRed = Color.FromArgb(239, 68, 68);
        private readonly Color TextPrimary = Color.FromArgb(244, 244, 245);
        private readonly Color TextMuted = Color.FromArgb(161, 161, 170);

        private Panel pnlSidebar;
        private Panel pnlContent;
        private List<Button> navButtons = new List<Button>();
        private List<Panel> pages = new List<Panel>();

        // Page 0: Map & GPS Controller
        private Label lblDriverBadge;
        private Label lblHostTzBadge;
        private Label lblApiCoords;
        private TextBox txtSearchCity;
        private WebBrowser wbMap;
        private TextBox txtLat;
        private TextBox txtLon;
        private ComboBox cbPresets;
        private RichTextBox rtbLog;

        // Page 1: Per-App Location
        private ListView lvApps;

        // Page 2: Isolated Launcher
        private ComboBox cbLauncherTz;
        private TextBox txtLauncherProxy;
        private TextBox txtCustomExe;
        private RichTextBox rtbLauncherLog;

        // Page 3: AI Discovery & Kill Switch
        private ListView lvDiscoveredAI;
        private Label lblKillSwitchBadge;
        private CheckBox chkAutoKill;
        private CheckBox chkFirewallGuard;
        private RichTextBox rtbKillSwitchLog;
        private List<DiscoveredAIApp> discoveredApps = new List<DiscoveredAIApp>();

        // Page 4: Security Audit
        private Label lblAuditGps;
        private Label lblAuditHostTz;
        private Label lblAuditIp;
        private Label lblAuditCountry;
        private Label lblAuditVerdict;
        private Button btnRunAudit;

        private System.Windows.Forms.Timer guardTimer;

        public MainForm()
        {
            EnableIE11WebBrowserEmulation();
            InitializeUI();
            RefreshDashboard();
            RefreshAppList();
            RunAIDiscovery();
            UpdateKillSwitchUI();
        }

        private void EnableIE11WebBrowserEmulation()
        {
            try
            {
                string exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                {
                    if (key != null) key.SetValue(exeName, 11001, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private void InitializeUI()
        {
            this.Text = "FakeGPS Studio — Windows 11 Spoofer & AI Shield";
            this.Size = new Size(1060, 740);
            this.MinimumSize = new Size(940, 640);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BgMain;
            this.ForeColor = TextPrimary;
            this.Font = new Font("Segoe UI", 9.5f);

            // Sidebar
            pnlSidebar = new Panel { Dock = DockStyle.Left, Width = 235, BackColor = BgSidebar };
            pnlSidebar.Paint += (s, e) =>
            {
                using (var pen = new Pen(BorderCard, 1))
                    e.Graphics.DrawLine(pen, pnlSidebar.Width - 1, 0, pnlSidebar.Width - 1, pnlSidebar.Height);
            };

            Label lblBrand = new Label
            {
                Text = "🛰️ FakeGPS Studio",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(18, 20),
                AutoSize = true
            };
            Label lblSubBrand = new Label
            {
                Text = "Win11 • Per-App • Zero-Leak",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = AccentEmerald,
                Location = new Point(21, 46),
                AutoSize = true
            };
            pnlSidebar.Controls.Add(lblBrand);
            pnlSidebar.Controls.Add(lblSubBrand);

            pnlContent = new Panel { Dock = DockStyle.Fill, BackColor = BgMain, Padding = new Padding(20) };

            this.Controls.Add(pnlContent);
            this.Controls.Add(pnlSidebar);

            // Create Pages
            var p0 = CreatePage();
            var p1 = CreatePage();
            var p2 = CreatePage();
            var p3 = CreatePage();
            var p4 = CreatePage();

            pages.AddRange(new Panel[] { p0, p1, p2, p3, p4 });
            foreach (var p in pages) pnlContent.Controls.Add(p);

            BuildMapControllerPage(p0);
            BuildPerAppPage(p1);
            BuildLauncherPage(p2);
            BuildAIDiscoveryKillSwitchPage(p3);
            BuildAuditPage(p4);

            // Sidebar Nav Buttons
            AddNavButton("🗺️  Карта и GPS", 0, 90);
            AddNavButton("🎯  Доступ приложений", 1, 140);
            AddNavButton("🚀  Изоляция (Timezone)", 2, 190);
            AddNavButton("🛡️  AI Kill Switch", 3, 240);
            AddNavButton("🔍  Аудит утечек", 4, 290);

            SelectPage(0);

            guardTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            guardTimer.Tick += GuardTimer_Tick;
            guardTimer.Start();
        }

        private Panel CreatePage()
        {
            return new Panel { Dock = DockStyle.Fill, BackColor = BgMain, Visible = false };
        }

        private void AddNavButton(string text, int pageIndex, int top)
        {
            var btn = new Button
            {
                Text = "  " + text,
                Location = new Point(12, top),
                Size = new Size(210, 42),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                ForeColor = TextMuted,
                BackColor = BgSidebar,
                Cursor = Cursors.Hand,
                Tag = pageIndex
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(35, 35, 45);
            btn.Click += (s, e) => SelectPage((int)((Button)s).Tag);
            navButtons.Add(btn);
            pnlSidebar.Controls.Add(btn);
        }

        private void SelectPage(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = (i == index);
                if (i == index)
                {
                    navButtons[i].BackColor = Color.FromArgb(42, 42, 56);
                    navButtons[i].ForeColor = Color.White;
                    navButtons[i].Font = new Font("Segoe UI", 10f, FontStyle.Bold);
                }
                else
                {
                    navButtons[i].BackColor = BgSidebar;
                    navButtons[i].ForeColor = TextMuted;
                    navButtons[i].Font = new Font("Segoe UI", 10f, FontStyle.Regular);
                }
            }
        }

        private Panel CreateCard(int x, int y, int w, int h, AnchorStyles anchor = AnchorStyles.Top | AnchorStyles.Left)
        {
            var card = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = BgCard,
                Anchor = anchor
            };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(BorderCard, 1))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };
            return card;
        }

        private Button CreateStyledButton(string text, int x, int y, int w, int h, Color bg)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = bg,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private void BuildMapControllerPage(Panel page)
        {
            // Status Header Card
            var cardStatus = CreateCard(0, 0, 780, 75, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            lblDriverBadge = new Label { Location = new Point(16, 12), AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Text = "● Проверка драйвера..." };
            lblHostTzBadge = new Label { Location = new Point(16, 42), AutoSize = true, ForeColor = TextMuted, Text = "Системный часовой пояс Windows: ..." };
            lblApiCoords = new Label { Location = new Point(360, 14), AutoSize = true, ForeColor = AccentEmerald, Font = new Font("Consolas", 9.5f), Text = "Windows API: ..." };

            var btnRefresh = CreateStyledButton("🔄 Обновить", 640, 18, 125, 36, Color.FromArgb(55, 55, 70));
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRefresh.Click += (s, e) => RefreshDashboard();

            cardStatus.Controls.AddRange(new Control[] { lblDriverBadge, lblHostTzBadge, lblApiCoords, btnRefresh });

            // Search & Presets Card
            var cardControls = CreateCard(0, 88, 780, 105, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);

            Label lblSearch = new Label { Text = "Поиск города (OpenStreetMap):", Location = new Point(16, 14), AutoSize = true, ForeColor = TextMuted };
            txtSearchCity = new TextBox { Location = new Point(215, 11), Width = 240, BackColor = BgMain, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            var btnSearch = CreateStyledButton("Найти", 465, 10, 80, 26, AccentIndigo);
            btnSearch.Click += (s, e) => SearchCityOnMap();

            cbPresets = new ComboBox { Location = new Point(560, 11), Width = 205, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgMain, ForeColor = Color.White };
            cbPresets.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var presets = new LatLong[]
            {
                new LatLong(52.379189, 4.899431, "🇳🇱 Амстердам (Нидерланды)", "Europe/Amsterdam"),
                new LatLong(51.924420, 4.477733, "🇳🇱 Роттердам (Нидерланды)", "Europe/Amsterdam"),
                new LatLong(52.070498, 4.300699, "🇳🇱 Гаага (Нидерланды)", "Europe/Amsterdam"),
                new LatLong(52.090737, 5.121420, "🇳🇱 Утрехт (Нидерланды)", "Europe/Amsterdam"),
                new LatLong(51.507351, -0.127758, "🇬🇧 Лондон", "Europe/London"),
                new LatLong(50.110922, 8.682127, "🇩🇪 Франкфурт", "Europe/Berlin"),
                new LatLong(40.712776, -74.005974, "🇺🇸 Нью-Йорк", "America/New_York"),
                new LatLong(35.676192, 139.650311, "🇯🇵 Токио", "Asia/Tokyo")
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
                    MoveMapMarker(sel.Latitude, sel.Longitude);
                }
            };

            Label lblLat = new Label { Text = "Широта:", Location = new Point(16, 56), AutoSize = true, ForeColor = TextMuted };
            txtLat = new TextBox { Location = new Point(78, 53), Width = 135, Text = "52.379189", BackColor = BgMain, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10f) };

            Label lblLon = new Label { Text = "Долгота:", Location = new Point(225, 56), AutoSize = true, ForeColor = TextMuted };
            txtLon = new TextBox { Location = new Point(290, 53), Width = 135, Text = "4.899431", BackColor = BgMain, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10f) };

            var btnApply = CreateStyledButton("🚀 Применить координаты GPS", 445, 48, 320, 36, AccentEmerald);
            btnApply.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnApply.Click += (s, e) => ApplyCoordinatesFromUI();

            cardControls.Controls.AddRange(new Control[] { lblSearch, txtSearchCity, btnSearch, cbPresets, lblLat, txtLat, lblLon, txtLon, btnApply });

            // Interactive Leaflet Map Card
            var cardMap = CreateCard(0, 205, 780, 345, AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
            wbMap = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true,
                IsWebBrowserContextMenuEnabled = false
            };
            wbMap.ObjectForScripting = new MapScriptBridge((lat, lon) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    txtLat.Text = lat.ToString("F6", CultureInfo.InvariantCulture);
                    txtLon.Text = lon.ToString("F6", CultureInfo.InvariantCulture);
                    Log(string.Format("Выбрана точка на карте: {0}, {1}", txtLat.Text, txtLon.Text));
                });
            });
            LoadLeafletMap(52.379189, 4.899431);
            cardMap.Controls.Add(wbMap);

            // Log Box
            rtbLog = new RichTextBox
            {
                Location = new Point(0, 560),
                Size = new Size(780, 95),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(14, 14, 18),
                ForeColor = TextMuted,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 8.5f),
                ReadOnly = true
            };

            page.Controls.AddRange(new Control[] { cardStatus, cardControls, cardMap, rtbLog });
        }

        private void LoadLeafletMap(double initLat, double initLon)
        {
            string html = string.Format(CultureInfo.InvariantCulture, @"<!DOCTYPE html>
<html>
<head>
<meta http-equiv='X-UA-Compatible' content='IE=edge' />
<meta charset='utf-8' />
<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css' />
<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
<style>
  html, body, #map {{ height: 100%; margin: 0; padding: 0; background: #141418; }}
  .leaflet-tile-pane {{ filter: brightness(0.92) contrast(1.05); }}
</style>
</head>
<body>
<div id='map'></div>
<script>
  var map = L.map('map').setView([{0}, {1}], 12);
  L.tileLayer('https://tile.openstreetmap.org/{{z}}/{{x}}/{{y}}.png', {{
      maxZoom: 19,
      attribution: '&copy; OpenStreetMap'
  }}).addTo(map);

  var marker = L.marker([{0}, {1}], {{ draggable: true }}).addTo(map);

  map.on('click', function(e) {{
      marker.setLatLng(e.latlng);
      if (window.external && window.external.OnMapClick) {{
          window.external.OnMapClick(e.latlng.lat, e.latlng.lng);
      }}
  }});

  marker.on('dragend', function(e) {{
      var pos = marker.getLatLng();
      if (window.external && window.external.OnMapClick) {{
          window.external.OnMapClick(pos.lat, pos.lng);
      }}
  }});

  function setMarker(lat, lon) {{
      var ll = L.latLng(lat, lon);
      marker.setLatLng(ll);
      map.setView(ll, 12);
  }}
</script>
</body>
</html>", initLat, initLon);

            wbMap.DocumentText = html;
        }

        private void MoveMapMarker(double lat, double lon)
        {
            try
            {
                if (wbMap.Document != null)
                {
                    wbMap.Document.InvokeScript("setMarker", new object[] { lat, lon });
                }
            }
            catch { }
        }

        private void SearchCityOnMap()
        {
            string query = txtSearchCity.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            Log("Поиск через OpenStreetMap Nominatim: " + query + "...");
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    var results = OpenMapGeocoder.Search(query);
                    this.Invoke((MethodInvoker)delegate
                    {
                        if (results.Count > 0)
                        {
                            var first = results[0];
                            txtLat.Text = first.Latitude.ToString("F6", CultureInfo.InvariantCulture);
                            txtLon.Text = first.Longitude.ToString("F6", CultureInfo.InvariantCulture);
                            MoveMapMarker(first.Latitude, first.Longitude);
                            Log("Найдено: " + first.Name);
                        }
                        else
                        {
                            Log("Локация не найдена по запросу: " + query);
                        }
                    });
                }
                catch (Exception ex)
                {
                    this.Invoke((MethodInvoker)delegate { Log("Ошибка поиска OpenStreetMap: " + ex.Message); });
                }
            });
        }

        private void ApplyCoordinatesFromUI()
        {
            try
            {
                double lat = double.Parse(txtLat.Text.Trim(), CultureInfo.InvariantCulture);
                double lon = double.Parse(txtLon.Text.Trim(), CultureInfo.InvariantCulture);

                string instanceId;
                FakeGPSCore.SetCoordinates(lat, lon, out instanceId);
                MoveMapMarker(lat, lon);
                Log(string.Format("Координаты {0}, {1} применены на сенсор {2}!", lat, lon, instanceId));
                RefreshDashboard();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Ошибка: " + ex.Message, "FakeGPS", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BuildPerAppPage(Panel page)
        {
            var cardTop = CreateCard(0, 0, 780, 95, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            Label lblInfo = new Label
            {
                Text = "Точечное разрешение Windows Location API (ConsentStore).\nПриложения с галочкой получают спуфленный GPS; без галочки — полностью заблокированы.",
                Location = new Point(16, 12),
                AutoSize = true,
                ForeColor = TextMuted
            };
            var btnScan = CreateStyledButton("🔄 Сканировать", 16, 52, 130, 30, Color.FromArgb(55, 55, 70));
            btnScan.Click += (s, e) => RefreshAppList();

            var btnAdd = CreateStyledButton("➕ Добавить .exe", 156, 52, 140, 30, AccentIndigo);
            btnAdd.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" })
                {
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        PerAppLocationManager.AddAppByPath(ofd.FileName, true);
                        RefreshAppList();
                    }
                }
            };

            var btnDenyAll = CreateStyledButton("Запретить всем", 620, 52, 140, 30, AccentRed);
            btnDenyAll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDenyAll.Click += (s, e) => { foreach (ListViewItem it in lvApps.Items) it.Checked = false; };

            cardTop.Controls.AddRange(new Control[] { lblInfo, btnScan, btnAdd, btnDenyAll });

            lvApps = new ListView
            {
                Location = new Point(0, 108),
                Size = new Size(780, 545),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                BackColor = BgCard,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.None
            };
            lvApps.Columns.Add("Доступ GPS", 120);
            lvApps.Columns.Add("Приложение", 190);
            lvApps.Columns.Add("Путь к файлу", 450);

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

            page.Controls.AddRange(new Control[] { cardTop, lvApps });
        }

        private void BuildLauncherPage(Panel page)
        {
            var cardInfo = CreateCard(0, 0, 780, 90, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            Label lblTitle = new Label
            {
                Text = "Изолированный запуск: отдельный Timezone + GPS без изменения системы",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                Location = new Point(16, 12),
                AutoSize = true
            };
            Label lblDesc = new Label
            {
                Text = "Часы и часовой пояс Windows не меняются. Подменяются только TZ/V8, WebView2 и отключаются WebRTC/Wi-Fi утечки.",
                Location = new Point(16, 44),
                AutoSize = true,
                ForeColor = TextMuted
            };
            cardInfo.Controls.AddRange(new Control[] { lblTitle, lblDesc });

            var cardCfg = CreateCard(0, 102, 780, 110, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            Label lblTz = new Label { Text = "Часовой пояс (TZ):", Location = new Point(16, 20), AutoSize = true, ForeColor = TextMuted };
            cbLauncherTz = new ComboBox { Location = new Point(190, 17), Width = 340, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgMain, ForeColor = Color.White };
            cbLauncherTz.Items.AddRange(new object[]
            {
                "Europe/Amsterdam (Нидерланды, CET UTC+1)",
                "Europe/London (Великобритания, GMT UTC+0)",
                "Europe/Berlin (Германия, CET UTC+1)",
                "America/New_York (США, EST UTC-5)",
                "Asia/Tokyo (Япония, JST UTC+9)"
            });
            cbLauncherTz.SelectedIndex = 0;

            Label lblProxy = new Label { Text = "SOCKS5 / HTTP Proxy:", Location = new Point(16, 62), AutoSize = true, ForeColor = TextMuted };
            txtLauncherProxy = new TextBox { Location = new Point(190, 59), Width = 340, BackColor = BgMain, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            Label lblHint = new Label { Text = "Опционально (пример: socks5://127.0.0.1:10808)", Location = new Point(540, 62), AutoSize = true, ForeColor = TextMuted };

            cardCfg.Controls.AddRange(new Control[] { lblTz, cbLauncherTz, lblProxy, txtLauncherProxy, lblHint });

            var cardActions = CreateCard(0, 224, 780, 150, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            var btnChatGPT = CreateStyledButton("🤖 Запустить ChatGPT (Isolated TZ)", 16, 20, 360, 44, AccentEmerald);
            btnChatGPT.Click += (s, e) =>
            {
                string tz = cbLauncherTz.SelectedItem.ToString().Split(' ')[0];
                string proxy = txtLauncherProxy.Text.Trim();
                string appx = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1111.2228.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
                if (File.Exists(appx))
                {
                    IsolatedAppLauncher.LaunchWithSpoofing(appx, "", tz, proxy);
                    LogLauncher("ChatGPT запущен с TZ=" + tz);
                }
                else
                {
                    string browser = IsolatedAppLauncher.FindBrowserPath();
                    if (!string.IsNullOrEmpty(browser))
                    {
                        IsolatedAppLauncher.LaunchWithSpoofing(browser, "https://chatgpt.com", tz, proxy);
                        LogLauncher("ChatGPT запущен в защищенном браузере с TZ=" + tz);
                    }
                }
            };

            var btnClaude = CreateStyledButton("🧠 Запустить Claude (Clean Profile + TZ)", 400, 20, 360, 44, AccentIndigo);
            btnClaude.Click += (s, e) =>
            {
                string tz = cbLauncherTz.SelectedItem.ToString().Split(' ')[0];
                string proxy = txtLauncherProxy.Text.Trim();
                string browser = IsolatedAppLauncher.FindBrowserPath();
                if (!string.IsNullOrEmpty(browser))
                {
                    string profile = Path.Combine(Path.GetTempPath(), "FakeGPS_Claude_Profile");
                    IsolatedAppLauncher.LaunchWithSpoofing(browser, string.Format("https://claude.ai --user-data-dir=\"{0}\"", profile), tz, proxy);
                    LogLauncher("Claude запущен в изолированном профиле [TZ=" + tz + "]");
                }
            };

            txtCustomExe = new TextBox { Location = new Point(16, 92), Width = 440, BackColor = BgMain, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            var btnBrowse = CreateStyledButton("Обзор...", 468, 90, 90, 28, Color.FromArgb(55, 55, 70));
            btnBrowse.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" })
                {
                    if (ofd.ShowDialog() == DialogResult.OK) txtCustomExe.Text = ofd.FileName;
                }
            };
            var btnRunCustom = CreateStyledButton("🚀 Запустить EXE", 570, 90, 190, 28, AccentEmerald);
            btnRunCustom.Click += (s, e) =>
            {
                if (File.Exists(txtCustomExe.Text.Trim()))
                {
                    string tz = cbLauncherTz.SelectedItem.ToString().Split(' ')[0];
                    IsolatedAppLauncher.LaunchWithSpoofing(txtCustomExe.Text.Trim(), "", tz, txtLauncherProxy.Text.Trim());
                    LogLauncher("Запущено: " + Path.GetFileName(txtCustomExe.Text) + " [TZ=" + tz + "]");
                }
            };

            cardActions.Controls.AddRange(new Control[] { btnChatGPT, btnClaude, txtCustomExe, btnBrowse, btnRunCustom });

            rtbLauncherLog = new RichTextBox
            {
                Location = new Point(0, 388),
                Size = new Size(780, 265),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(14, 14, 18),
                ForeColor = Color.FromArgb(96, 165, 250),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                ReadOnly = true
            };

            page.Controls.AddRange(new Control[] { cardInfo, cardCfg, cardActions, rtbLauncherLog });
        }

        private void BuildAIDiscoveryKillSwitchPage(Panel page)
        {
            var cardTop = CreateCard(0, 0, 780, 130, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            lblKillSwitchBadge = new Label { Location = new Point(16, 12), AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };

            var btnPanic = CreateStyledButton("🚨 АВАРИЙНЫЙ KILL SWITCH (Убить выбранные AI + Блок Firewall)", 16, 48, 490, 42, AccentRed);
            btnPanic.Click += (s, e) =>
            {
                int killed = AIKillSwitch.TerminateDiscoveredAI(discoveredApps);
                AIKillSwitch.SetFirewallBlock(true, discoveredApps);
                LogKillSwitch(string.Format("🚨 KILL SWITCH АКТИВИРОВАН! Завершено процессов: {0}. Трафик заблокирован.", killed));
                UpdateKillSwitchUI();
                RunAIDiscovery();
            };

            var btnUnlock = CreateStyledButton("Снять блок Firewall", 518, 48, 140, 42, Color.FromArgb(55, 55, 70));
            btnUnlock.Click += (s, e) =>
            {
                AIKillSwitch.SetFirewallBlock(false, discoveredApps);
                LogKillSwitch("Сетевая блокировка Firewall снята.");
                UpdateKillSwitchUI();
            };

            var btnRescan = CreateStyledButton("🔄 Найти AI", 668, 48, 96, 42, AccentIndigo);
            btnRescan.Click += (s, e) => RunAIDiscovery();

            chkAutoKill = new CheckBox { Text = "Авто-убийство процессов при сбое FakeGPS", Location = new Point(16, 98), AutoSize = true, Checked = true };
            chkFirewallGuard = new CheckBox { Text = "Авто-блокировка Firewall при сбое датчика", Location = new Point(360, 98), AutoSize = true, Checked = false };

            cardTop.Controls.AddRange(new Control[] { lblKillSwitchBadge, btnPanic, btnUnlock, btnRescan, chkAutoKill, chkFirewallGuard });

            lvDiscoveredAI = new ListView
            {
                Location = new Point(0, 142),
                Size = new Size(780, 320),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                BackColor = BgCard,
                ForeColor = TextPrimary,
                BorderStyle = BorderStyle.None
            };
            lvDiscoveredAI.Columns.Add("Под защитой", 95);
            lvDiscoveredAI.Columns.Add("AI Приложение / Вкладка", 230);
            lvDiscoveredAI.Columns.Add("Тип", 130);
            lvDiscoveredAI.Columns.Add("Статус", 100);
            lvDiscoveredAI.Columns.Add("Путь", 210);

            lvDiscoveredAI.ItemChecked += (s, e) =>
            {
                if (e.Item.Tag is DiscoveredAIApp)
                {
                    ((DiscoveredAIApp)e.Item.Tag).SelectedForKillSwitch = e.Item.Checked;
                }
            };

            rtbKillSwitchLog = new RichTextBox
            {
                Location = new Point(0, 474),
                Size = new Size(780, 180),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(14, 14, 18),
                ForeColor = AccentEmerald,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                ReadOnly = true
            };

            page.Controls.AddRange(new Control[] { cardTop, lvDiscoveredAI, rtbKillSwitchLog });
        }

        private void BuildAuditPage(Panel page)
        {
            var card = CreateCard(0, 0, 780, 340, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
            Label lblTitle = new Label { Text = "Аудит маскировки: GPS vs Системный TZ vs Публичный IP", Font = new Font("Segoe UI", 12f, FontStyle.Bold), Location = new Point(20, 20), AutoSize = true };

            lblAuditGps = new Label { Location = new Point(20, 70), AutoSize = true, Text = "1. Сенсор FakeGPS Windows: ..." };
            lblAuditHostTz = new Label { Location = new Point(20, 105), AutoSize = true, Text = "2. Системный Timezone Windows: ..." };
            lblAuditIp = new Label { Location = new Point(20, 140), AutoSize = true, Text = "3. Публичный IP-адрес: ..." };
            lblAuditCountry = new Label { Location = new Point(20, 175), AutoSize = true, Text = "4. Страна по IP: ..." };
            lblAuditVerdict = new Label { Location = new Point(20, 220), AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Text = "Нажмите кнопку для запуска проверки" };

            btnRunAudit = CreateStyledButton("🔎 Запустить полный аудит", 20, 275, 240, 42, AccentIndigo);
            btnRunAudit.Click += (s, e) => RunSecurityAudit();

            card.Controls.AddRange(new Control[] { lblTitle, lblAuditGps, lblAuditHostTz, lblAuditIp, lblAuditCountry, lblAuditVerdict, btnRunAudit });
            page.Controls.Add(card);
        }

        private void RefreshDashboard()
        {
            bool active = FakeGPSCore.IsDriverActive();
            lblDriverBadge.Text = active ? "● Драйвер FakeGPS: АКТИВЕН (OK)" : "● Драйвер FakeGPS: ОТКЛЮЧЕН / ОШИБКА";
            lblDriverBadge.ForeColor = active ? AccentEmerald : AccentRed;
            lblHostTzBadge.Text = "Системный TZ Windows: " + TimeZoneInfo.Local.Id + " (не затрагивается при изолированном запуске)";

            var cfg = FakeGPSCore.GetConfiguredCoordinates();
            if (cfg != null)
            {
                txtLat.Text = cfg.Latitude.ToString("F6", CultureInfo.InvariantCulture);
                txtLon.Text = cfg.Longitude.ToString("F6", CultureInfo.InvariantCulture);
            }

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    var loc = FakeGPSCore.QueryWindowsLocationApi();
                    this.Invoke((MethodInvoker)delegate
                    {
                        lblApiCoords.Text = string.Format("Windows API: {0}, {1}",
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
            foreach (var a in PerAppLocationManager.GetApps())
            {
                var lvi = new ListViewItem(a.IsAllowed ? "✅ Разрешено" : "❌ Запрещено");
                lvi.Checked = a.IsAllowed;
                lvi.SubItems.Add(a.DisplayName);
                lvi.SubItems.Add(a.ExePath);
                lvi.Tag = a;
                lvApps.Items.Add(lvi);
            }
        }

        private void RunAIDiscovery()
        {
            lvDiscoveredAI.Items.Clear();
            discoveredApps = AIDiscoveryEngine.DiscoverAll();
            foreach (var app in discoveredApps)
            {
                var lvi = new ListViewItem(app.SelectedForKillSwitch ? "Вкл" : "Выкл");
                lvi.Checked = app.SelectedForKillSwitch;
                lvi.SubItems.Add(app.Name);
                lvi.SubItems.Add(app.Category);
                lvi.SubItems.Add(app.IsRunning ? "🟢 Работает" : "⚪ Установлено");
                lvi.SubItems.Add(app.ExecutablePath);
                lvi.Tag = app;
                lvDiscoveredAI.Items.Add(lvi);
            }
            LogKillSwitch(string.Format("Автообнаружение: найдено {0} AI-приложений/процессов.", discoveredApps.Count));
        }

        private void UpdateKillSwitchUI()
        {
            bool blocked = AIKillSwitch.IsFirewallBlockActive();
            lblKillSwitchBadge.Text = blocked ? "● AI Guard: СЕТЬ ЗАБЛОКИРОВАНА (FIREWALL)" : "● AI Guard: АКТИВЕН (Мониторинг)";
            lblKillSwitchBadge.ForeColor = blocked ? AccentRed : AccentEmerald;
        }

        private void GuardTimer_Tick(object sender, EventArgs e)
        {
            bool active = FakeGPSCore.IsDriverActive();
            if (!active)
            {
                if (chkAutoKill.Checked)
                {
                    int killed = AIKillSwitch.TerminateDiscoveredAI(discoveredApps);
                    if (killed > 0) LogKillSwitch(string.Format("⚠️ Сбой драйвера! Закрыто AI-процессов: {0}", killed));
                }
                if (chkFirewallGuard.Checked && !AIKillSwitch.IsFirewallBlockActive())
                {
                    AIKillSwitch.SetFirewallBlock(true, discoveredApps);
                    UpdateKillSwitchUI();
                }
            }
        }

        private void RunSecurityAudit()
        {
            btnRunAudit.Enabled = false;
            lblAuditVerdict.Text = "Проверка...";

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
                    lblAuditGps.Text = string.Format("1. Сенсор FakeGPS Windows: {0}, {1}",
                        loc.Latitude.ToString("F4", CultureInfo.InvariantCulture),
                        loc.Longitude.ToString("F4", CultureInfo.InvariantCulture));
                    lblAuditHostTz.Text = "2. Системный Timezone Windows: " + TimeZoneInfo.Local.Id;
                    lblAuditIp.Text = "3. Публичный IP-адрес: " + publicIp;
                    lblAuditCountry.Text = "4. Страна по IP: " + country;

                    bool gpsIsNl = (loc.Latitude >= 50.7 && loc.Latitude <= 53.6 && loc.Longitude >= 3.3 && loc.Longitude <= 7.3);
                    bool ipIsNl = country.IndexOf("Netherlands", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (gpsIsNl && ipIsNl)
                    {
                        lblAuditVerdict.Text = "✅ ИДЕАЛЬНО: И GPS, и IP указывают на Нидерланды. Используйте Изолированный лаунчер для TZ!";
                        lblAuditVerdict.ForeColor = AccentEmerald;
                    }
                    else if (gpsIsNl)
                    {
                        lblAuditVerdict.Text = string.Format("⚠️ РАСХОЖДЕНИЕ: GPS в Нидерландах, но IP из '{0}'. Включите NL VPN или Proxy!", country);
                        lblAuditVerdict.ForeColor = Color.FromArgb(245, 158, 11);
                    }
                    else
                    {
                        lblAuditVerdict.Text = "❌ GPS не установлен на Нидерланды.";
                        lblAuditVerdict.ForeColor = AccentRed;
                    }
                    btnRunAudit.Enabled = true;
                });
            });
        }

        private void Log(string msg) { rtbLog.AppendText(string.Format("[{0}] {1}\n", DateTime.Now.ToString("HH:mm:ss"), msg)); rtbLog.ScrollToCaret(); }
        private void LogLauncher(string msg) { rtbLauncherLog.AppendText(string.Format("[{0}] {1}\n", DateTime.Now.ToString("HH:mm:ss"), msg)); rtbLauncherLog.ScrollToCaret(); }
        private void LogKillSwitch(string msg) { rtbKillSwitchLog.AppendText(string.Format("[{0}] {1}\n", DateTime.Now.ToString("HH:mm:ss"), msg)); rtbKillSwitchLog.ScrollToCaret(); }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}

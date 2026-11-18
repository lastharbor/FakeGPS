using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace FakeGPS.UI
{
    public class VlessNode
    {
        public string Name { get; set; }
        public string Uuid { get; set; }
        public string Server { get; set; }
        public int Port { get; set; }
        public string Security { get; set; }
        public string Sni { get; set; }
        public string Fingerprint { get; set; }
        public string PublicKey { get; set; }
        public string ShortId { get; set; }
        public string Flow { get; set; }
        public string NetworkType { get; set; }
        public string Path { get; set; }
        public string Host { get; set; }
        public string ServiceName { get; set; }
        public string RawUri { get; set; }

        public override string ToString()
        {
            return string.Format("{0} ({1}:{2})", string.IsNullOrEmpty(Name) ? Server : Name, Server, Port);
        }
    }

    public class AmneziaDecodedConfig
    {
        public string Kind { get; set; } // "xray", "awg", "wireguard"
        public string RawConfig { get; set; }
        public VlessNode Vless { get; set; }
        public string Endpoint { get; set; }
        public string ObfuscationSummary { get; set; }
    }

    public static class SubscriptionParser
    {
        public static List<VlessNode> FetchAndParse(string input)
        {
            var nodes = new List<VlessNode>();
            if (string.IsNullOrWhiteSpace(input)) return nodes;

            string trimmed = input.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers.Add("User-Agent", "sing-box/1.14.4");
                    string body = wc.DownloadString(trimmed).Trim();
                    string decoded = TryDecodeBase64(body);
                    foreach (var line in decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var node = ParseVlessUri(line.Trim());
                        if (node != null) nodes.Add(node);
                    }
                }
            }
            else
            {
                string decoded = trimmed.Contains("vless://") ? trimmed : TryDecodeBase64(trimmed);
                foreach (var line in decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var node = ParseVlessUri(line.Trim());
                    if (node != null) nodes.Add(node);
                }
            }

            return nodes;
        }

        public static string TryDecodeBase64(string text)
        {
            try
            {
                string clean = text.Trim().Replace('-', '+').Replace('_', '/');
                int mod = clean.Length % 4;
                if (mod > 0) clean += new string('=', 4 - mod);
                byte[] bytes = Convert.FromBase64String(clean);
                string s = Encoding.UTF8.GetString(bytes);
                if (s.Contains("vless://") || s.Contains("://")) return s;
            }
            catch { }
            return text;
        }

        public static VlessNode ParseVlessUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                string work = uri.Substring("vless://".Length);
                string fragment = "";
                int hashIdx = work.IndexOf('#');
                if (hashIdx >= 0)
                {
                    fragment = Uri.UnescapeDataString(work.Substring(hashIdx + 1));
                    work = work.Substring(0, hashIdx);
                }

                string query = "";
                int qIdx = work.IndexOf('?');
                if (qIdx >= 0)
                {
                    query = work.Substring(qIdx + 1);
                    work = work.Substring(0, qIdx);
                }

                int atIdx = work.IndexOf('@');
                if (atIdx < 0) return null;

                string uuid = work.Substring(0, atIdx);
                string hostPort = work.Substring(atIdx + 1);

                string host;
                int port = 443;
                if (hostPort.StartsWith("["))
                {
                    int closeBracket = hostPort.IndexOf(']');
                    host = hostPort.Substring(1, closeBracket - 1);
                    if (closeBracket + 2 < hostPort.Length)
                        int.TryParse(hostPort.Substring(closeBracket + 2), out port);
                }
                else
                {
                    int colon = hostPort.LastIndexOf(':');
                    host = colon > 0 ? hostPort.Substring(0, colon) : hostPort;
                    if (colon > 0) int.TryParse(hostPort.Substring(colon + 1), out port);
                }

                var qParams = ParseQuery(query);
                return new VlessNode
                {
                    Name = string.IsNullOrEmpty(fragment) ? host : fragment,
                    Uuid = uuid,
                    Server = host,
                    Port = port,
                    Security = GetParam(qParams, "security", "none"),
                    Sni = GetParam(qParams, "sni", host),
                    Fingerprint = GetParam(qParams, "fp", "chrome"),
                    PublicKey = GetParam(qParams, "pbk", ""),
                    ShortId = GetParam(qParams, "sid", ""),
                    Flow = GetParam(qParams, "flow", ""),
                    NetworkType = GetParam(qParams, "type", "tcp"),
                    Path = GetParam(qParams, "path", "/"),
                    Host = GetParam(qParams, "host", ""),
                    ServiceName = GetParam(qParams, "serviceName", ""),
                    RawUri = uri
                };
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            foreach (var part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0)
                {
                    string k = Uri.UnescapeDataString(part.Substring(0, eq));
                    string v = Uri.UnescapeDataString(part.Substring(eq + 1));
                    dict[k] = v;
                }
            }
            return dict;
        }

        private static string GetParam(Dictionary<string, string> dict, string key, string defVal)
        {
            string val;
            return dict.TryGetValue(key, out val) && !string.IsNullOrEmpty(val) ? val : defVal;
        }
    }

    public static class AmneziaDecoder
    {
        public static AmneziaDecodedConfig Decode(string input)
        {
            string trimmed = (input ?? "").Trim();
            if (trimmed.StartsWith("vpn://", StringComparison.OrdinalIgnoreCase))
            {
                string payload = trimmed.Substring("vpn://".Length).Replace('-', '+').Replace('_', '/');
                int mod = payload.Length % 4;
                if (mod > 0) payload += new string('=', 4 - mod);
                byte[] raw = Convert.FromBase64String(payload);

                // Qt qCompress format: 4-byte big-endian length + zlib stream (2-byte header 0x78 0x9C)
                int offset = 0;
                if (raw.Length > 6 && raw[4] == 0x78) offset = 6;
                else if (raw.Length > 2 && raw[0] == 0x78) offset = 2;

                string json;
                if (offset > 0)
                {
                    using (var ms = new MemoryStream(raw, offset, raw.Length - offset))
                    using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                    using (var sr = new StreamReader(ds, Encoding.UTF8))
                    {
                        json = sr.ReadToEnd();
                    }
                }
                else
                {
                    json = Encoding.UTF8.GetString(raw);
                }

                // Check if xray/vless config inside
                var mLastCfg = Regex.Match(json, "\"last_config\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
                if (mLastCfg.Success)
                {
                    string inner = Regex.Unescape(mLastCfg.Groups[1].Value);
                    if (inner.Contains("[Interface]") && inner.Contains("[Peer]"))
                    {
                        return ParseWgConf(inner);
                    }
                    if (inner.Contains("\"vless\""))
                    {
                        var node = ExtractVlessFromXrayJson(inner);
                        return new AmneziaDecodedConfig
                        {
                            Kind = "xray",
                            RawConfig = inner,
                            Vless = node,
                            Endpoint = node != null ? string.Format("{0}:{1}", node.Server, node.Port) : "Xray",
                            ObfuscationSummary = "Xray / VLESS внутри vpn://"
                        };
                    }
                }

                return new AmneziaDecodedConfig
                {
                    Kind = "json",
                    RawConfig = json,
                    Endpoint = "Amnezia JSON",
                    ObfuscationSummary = "Декодирован JSON контейнер AmneziaVPN"
                };
            }

            if (trimmed.Contains("[Interface]") && trimmed.Contains("[Peer]"))
            {
                return ParseWgConf(trimmed);
            }

            throw new ArgumentException("Неизвестный формат. Вставьте vpn:// ссылку AmneziaVPN или .conf конфиг AmneziaWG.");
        }

        private static AmneziaDecodedConfig ParseWgConf(string conf)
        {
            var mEp = Regex.Match(conf, @"Endpoint\s*=\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            var mJc = Regex.Match(conf, @"^\s*Jc\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var mS1 = Regex.Match(conf, @"^\s*S1\s*=\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            bool isAwg = mJc.Success || mS1.Success;

            return new AmneziaDecodedConfig
            {
                Kind = isAwg ? "awg" : "wireguard",
                RawConfig = conf,
                Endpoint = mEp.Success ? mEp.Groups[1].Value.Trim() : "Unknown",
                ObfuscationSummary = isAwg
                    ? string.Format("AmneziaWG (Jc={0}, S1={1})", mJc.Success ? mJc.Groups[1].Value : "?", mS1.Success ? mS1.Groups[1].Value : "?")
                    : "Standard WireGuard"
            };
        }

        private static VlessNode ExtractVlessFromXrayJson(string json)
        {
            var mAddr = Regex.Match(json, "\"address\"\\s*:\\s*\"([^\"]+)\"");
            var mPort = Regex.Match(json, "\"port\"\\s*:\\s*(\\d+)");
            var mId = Regex.Match(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            var mSni = Regex.Match(json, "\"serverName\"\\s*:\\s*\"([^\"]+)\"");
            var mPbk = Regex.Match(json, "\"publicKey\"\\s*:\\s*\"([^\"]+)\"");
            var mSid = Regex.Match(json, "\"shortId\"\\s*:\\s*\"([^\"]+)\"");
            var mFlow = Regex.Match(json, "\"flow\"\\s*:\\s*\"([^\"]+)\"");

            if (!mAddr.Success || !mId.Success) return null;
            int port = 443;
            if (mPort.Success) int.TryParse(mPort.Groups[1].Value, out port);

            return new VlessNode
            {
                Name = "Amnezia-VLESS",
                Server = mAddr.Groups[1].Value,
                Port = port,
                Uuid = mId.Groups[1].Value,
                Security = mPbk.Success ? "reality" : "tls",
                Sni = mSni.Success ? mSni.Groups[1].Value : mAddr.Groups[1].Value,
                PublicKey = mPbk.Success ? mPbk.Groups[1].Value : "",
                ShortId = mSid.Success ? mSid.Groups[1].Value : "",
                Flow = mFlow.Success ? mFlow.Groups[1].Value : "",
                Fingerprint = "chrome",
                NetworkType = "tcp"
            };
        }
    }

    public static class SingBoxManager
    {
        private static Process activeProcess;
        public static readonly string CoresDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cores");
        public static readonly string SingBoxExe = Path.Combine(CoresDir, "sing-box.exe");
        public static readonly string ConfigPath = Path.Combine(CoresDir, "singbox_active.json");

        public static bool IsInstalled()
        {
            return File.Exists(SingBoxExe);
        }

        public static bool IsRunning()
        {
            if (activeProcess != null && !activeProcess.HasExited) return true;
            try
            {
                return Process.GetProcessesByName("sing-box").Length > 0;
            }
            catch { return false; }
        }

        public static string BuildConfig(VlessNode node, bool enablePerAppTun, List<string> tunProcessNames)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"log\": { \"level\": \"info\", \"timestamp\": true },");
            sb.AppendLine("  \"dns\": {");
            sb.AppendLine("    \"servers\": [");
            sb.AppendLine("      { \"tag\": \"remote-dns\", \"type\": \"https\", \"server\": \"1.1.1.1\", \"detour\": \"proxy\" },");
            sb.AppendLine("      { \"tag\": \"local-dns\", \"type\": \"udp\", \"server\": \"77.88.8.8\" }");
            sb.AppendLine("    ],");
            sb.AppendLine("    \"final\": \"remote-dns\",");
            sb.AppendLine("    \"strategy\": \"ipv4_only\"");
            sb.AppendLine("  },");
            sb.AppendLine("  \"inbounds\": [");
            sb.AppendLine("    {");
            sb.AppendLine("      \"type\": \"mixed\",");
            sb.AppendLine("      \"tag\": \"mixed-in\",");
            sb.AppendLine("      \"listen\": \"127.0.0.1\",");
            sb.AppendLine("      \"listen_port\": 10808");
            sb.AppendLine("    }");
            if (enablePerAppTun)
            {
                sb.AppendLine("    ,{");
                sb.AppendLine("      \"type\": \"tun\",");
                sb.AppendLine("      \"tag\": \"tun-in\",");
                sb.AppendLine("      \"address\": [\"172.19.0.1/30\"],");
                sb.AppendLine("      \"mtu\": 1400,");
                sb.AppendLine("      \"auto_route\": true,");
                sb.AppendLine("      \"strict_route\": true,");
                sb.AppendLine("      \"stack\": \"gvisor\"");
                sb.AppendLine("    }");
            }
            sb.AppendLine("  ],");
            sb.AppendLine("  \"outbounds\": [");
            sb.AppendLine("    { \"type\": \"direct\", \"tag\": \"direct\" },");
            sb.AppendLine("    {");
            sb.AppendLine("      \"type\": \"vless\",");
            sb.AppendLine("      \"tag\": \"proxy\",");
            sb.AppendLine(string.Format("      \"server\": \"{0}\",", EscapeJson(node.Server)));
            sb.AppendLine(string.Format("      \"server_port\": {0},", node.Port));
            sb.AppendLine(string.Format("      \"uuid\": \"{0}\"", EscapeJson(node.Uuid)));
            if (!string.IsNullOrEmpty(node.Flow))
                sb.AppendLine(string.Format("      ,\"flow\": \"{0}\"", EscapeJson(node.Flow)));

            if (node.Security == "tls" || node.Security == "reality")
            {
                sb.AppendLine("      ,\"tls\": {");
                sb.AppendLine("        \"enabled\": true,");
                sb.AppendLine(string.Format("        \"server_name\": \"{0}\",", EscapeJson(node.Sni)));
                sb.AppendLine(string.Format("        \"utls\": {{ \"enabled\": true, \"fingerprint\": \"{0}\" }}", EscapeJson(node.Fingerprint)));
                if (node.Security == "reality" && !string.IsNullOrEmpty(node.PublicKey))
                {
                    sb.AppendLine("        ,\"reality\": {");
                    sb.AppendLine("          \"enabled\": true,");
                    sb.AppendLine(string.Format("          \"public_key\": \"{0}\",", EscapeJson(node.PublicKey)));
                    sb.AppendLine(string.Format("          \"short_id\": \"{0}\"", EscapeJson(node.ShortId)));
                    sb.AppendLine("        }");
                }
                sb.AppendLine("      }");
            }

            if (node.NetworkType == "ws")
            {
                sb.AppendLine("      ,\"transport\": {");
                sb.AppendLine("        \"type\": \"ws\",");
                sb.AppendLine(string.Format("        \"path\": \"{0}\"", EscapeJson(node.Path)));
                if (!string.IsNullOrEmpty(node.Host))
                    sb.AppendLine(string.Format("        ,\"headers\": {{ \"Host\": \"{0}\" }}", EscapeJson(node.Host)));
                sb.AppendLine("      }");
            }
            else if (node.NetworkType == "grpc")
            {
                sb.AppendLine("      ,\"transport\": {");
                sb.AppendLine("        \"type\": \"grpc\",");
                sb.AppendLine(string.Format("        \"service_name\": \"{0}\"", EscapeJson(node.ServiceName)));
                sb.AppendLine("      }");
            }

            sb.AppendLine("    }");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"route\": {");
            sb.AppendLine("    \"default_domain_resolver\": \"local-dns\",");
            sb.AppendLine("    \"rules\": [");
            sb.AppendLine("      { \"action\": \"sniff\" },");
            sb.AppendLine("      { \"protocol\": \"dns\", \"action\": \"hijack-dns\" },");
            sb.AppendLine("      { \"ip_is_private\": true, \"outbound\": \"direct\" }");
            if (enablePerAppTun && tunProcessNames != null && tunProcessNames.Count > 0)
            {
                var quoted = new List<string>();
                foreach (var p in tunProcessNames)
                {
                    string exe = p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : p + ".exe";
                    quoted.Add("\"" + EscapeJson(exe) + "\"");
                }
                sb.AppendLine(string.Format("      ,{{ \"process_name\": [{0}], \"outbound\": \"proxy\" }}", string.Join(", ", quoted.ToArray())));
                sb.AppendLine("    ],");
                sb.AppendLine("    \"final\": \"direct\"");
            }
            else
            {
                sb.AppendLine("    ],");
                sb.AppendLine("    \"final\": \"proxy\"");
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static void Start(VlessNode node, bool enablePerAppTun, List<string> tunProcesses, Action<string> onLog)
        {
            Stop();
            if (!IsInstalled())
                throw new FileNotFoundException("sing-box.exe не найден в " + SingBoxExe);

            string cfgJson = BuildConfig(node, enablePerAppTun, tunProcesses);
            File.WriteAllText(ConfigPath, cfgJson, new UTF8Encoding(false));

            // Validate first
            var chk = new ProcessStartInfo
            {
                FileName = SingBoxExe,
                Arguments = string.Format("check -c \"{0}\"", ConfigPath),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using (var p = Process.Start(chk))
            {
                string err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("Ошибка конфига sing-box: " + err);
            }

            var psi = new ProcessStartInfo
            {
                FileName = SingBoxExe,
                Arguments = string.Format("run -c \"{0}\"", ConfigPath),
                WorkingDirectory = CoresDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            activeProcess = new Process();
            activeProcess.StartInfo = psi;
            activeProcess.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && onLog != null) onLog(e.Data); };
            activeProcess.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && onLog != null) onLog(e.Data); };
            activeProcess.Start();
            activeProcess.BeginOutputReadLine();
            activeProcess.BeginErrorReadLine();
        }

        public static void Stop()
        {
            try
            {
                if (activeProcess != null && !activeProcess.HasExited)
                {
                    activeProcess.Kill();
                    activeProcess.WaitForExit(2000);
                }
            }
            catch { }

            try
            {
                foreach (var p in Process.GetProcessesByName("sing-box"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    public class LeakAuditReport
    {
        public string GpsStatus { get; set; }
        public bool GpsIsNetherlands { get; set; }
        public string HostTimezone { get; set; }
        public string DirectIp { get; set; }
        public string DirectCountry { get; set; }
        public string ProxyIp { get; set; }
        public string ProxyCountry { get; set; }
        public bool SingBoxRunning { get; set; }
        public bool HasGlobalIPv6 { get; set; }
        public string IPv6Addresses { get; set; }
        public string ActiveDnsServers { get; set; }
        public bool WifiEnabled { get; set; }
        public string Verdict { get; set; }
        public int RiskScore { get; set; } // 0 = zero leaks, 100 = high risk
    }

    public static class DeepLeakAuditor
    {
        public static LeakAuditReport RunFullAudit(LatLong gpsLoc)
        {
            var r = new LeakAuditReport();
            r.HostTimezone = TimeZoneInfo.Local.Id;
            r.SingBoxRunning = SingBoxManager.IsRunning();

            r.GpsIsNetherlands = (gpsLoc.Latitude >= 50.7 && gpsLoc.Latitude <= 53.6 &&
                                  gpsLoc.Longitude >= 3.3 && gpsLoc.Longitude <= 7.3);
            r.GpsStatus = string.Format("{0}, {1} ({2})",
                gpsLoc.Latitude.ToString("F4", CultureInfo.InvariantCulture),
                gpsLoc.Longitude.ToString("F4", CultureInfo.InvariantCulture),
                r.GpsIsNetherlands ? "Нидерланды" : "Другой регион");

            // Direct IP
            string dIp, dCountry;
            QueryIpInfo(null, out dIp, out dCountry);
            r.DirectIp = dIp;
            r.DirectCountry = dCountry;

            // Proxy / sing-box IP (127.0.0.1:10808)
            if (r.SingBoxRunning)
            {
                string pIp, pCountry;
                QueryIpInfo("http://127.0.0.1:10808", out pIp, out pCountry);
                r.ProxyIp = pIp;
                r.ProxyCountry = pCountry;
            }
            else
            {
                r.ProxyIp = "Ядро остановлено";
                r.ProxyCountry = "-";
            }

            // Network Interface Audit: IPv6, DNS, Wi-Fi
            var ipv6List = new List<string>();
            var dnsList = new List<string>();
            bool wifiUp = false;

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                        wifiUp = true;

                    var props = ni.GetIPProperties();
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                            !ua.Address.IsIPv6LinkLocal &&
                            !IPAddress.IsLoopback(ua.Address))
                        {
                            ipv6List.Add(ua.Address.ToString());
                        }
                    }

                    foreach (var dns in props.DnsAddresses)
                    {
                        if (dns.AddressFamily == AddressFamily.InterNetwork)
                            dnsList.Add(dns.ToString());
                    }
                }
            }
            catch { }

            r.HasGlobalIPv6 = ipv6List.Count > 0;
            r.IPv6Addresses = r.HasGlobalIPv6 ? string.Join(", ", ipv6List.ToArray()) : "Нет (Безопасно)";
            r.ActiveDnsServers = dnsList.Count > 0 ? string.Join(", ", dnsList.ToArray()) : "Не обнаружены";
            r.WifiEnabled = wifiUp;

            // Calculate Risk Verdict
            int risks = 0;
            var notes = new List<string>();

            if (!r.GpsIsNetherlands) { risks += 30; notes.Add("GPS не в Нидерландах"); }
            if (!r.SingBoxRunning)
            {
                bool directNl = (r.DirectCountry ?? "").IndexOf("Netherlands", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!directNl) { risks += 40; notes.Add("sing-box не запущен, прямой IP не из Нидерландов"); }
            }
            else
            {
                bool proxyNl = (r.ProxyCountry ?? "").IndexOf("Netherlands", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!proxyNl) { risks += 35; notes.Add("Выходной IP туннеля (" + r.ProxyCountry + ") не из Нидерландов"); }
            }

            if (r.HasGlobalIPv6) { risks += 15; notes.Add("Активен глобальный IPv6 (возможна утечка мимо IPv4-прокси)"); }
            if (r.WifiEnabled) { risks += 10; notes.Add("Включен Wi-Fi (используйте --disable-features=GeolocationBssid)"); }

            r.RiskScore = Math.Min(100, risks);
            if (risks == 0)
                r.Verdict = "✅ ИДЕАЛЬНО (Риск утечек 0%): GPS и Туннель в Нидерландах, IPv6 не течет, системный TZ не затронут.";
            else
                r.Verdict = string.Format("⚠️ ОБНАРУЖЕНЫ РИСКИ ({0}%): {1}", r.RiskScore, string.Join("; ", notes.ToArray()));

            return r;
        }

        private static void QueryIpInfo(string httpProxy, out string ip, out string country)
        {
            ip = "Ошибка/Таймаут";
            country = "Неизвестно";
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "FakeGPS-Audit/2.0");
                    if (!string.IsNullOrEmpty(httpProxy))
                        wc.Proxy = new WebProxy(httpProxy);

                    string json = wc.DownloadString("http://ip-api.com/json");
                    var mIp = Regex.Match(json, "\"query\"\\s*:\\s*\"([^\"]+)\"");
                    var mCountry = Regex.Match(json, "\"country\"\\s*:\\s*\"([^\"]+)\"");
                    if (mIp.Success) ip = mIp.Groups[1].Value;
                    if (mCountry.Success) country = mCountry.Groups[1].Value;
                }
            }
            catch { }
        }
    }
}

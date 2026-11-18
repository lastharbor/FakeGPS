using System;
using System.Diagnostics;
using System.Globalization;
using System.Device.Location;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace FakeGPS
{
    public class LatLong
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public static class RegistryHelper
    {
        private const string LatitudeProperty = "SENSOR_PROPERTY_LATITUDE";
        private const string LongitudeProperty = "SENSOR_PROPERTY_LONGITUDE";

        public static string FindDevicePath(out string instanceId)
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

        public static void SetLatLong(LatLong latLong, out string instanceId)
        {
            string path = FindDevicePath(out instanceId);
            using (var key = Registry.LocalMachine.OpenSubKey(path, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException(
                        string.Format("FakeGPS registry key not found at 'HKLM\\{0}'.\nPlease ensure FakeGPS driver is installed (ROOT\\SENSOR\\0000).", path));
                }

                key.SetValue(LatitudeProperty, latLong.Latitude.ToString(CultureInfo.InvariantCulture));
                key.SetValue(LongitudeProperty, latLong.Longitude.ToString(CultureInfo.InvariantCulture));
            }
        }

        public static LatLong GetConfiguredLatLong(out string instanceId)
        {
            string path = FindDevicePath(out instanceId);
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
                        return new LatLong { Latitude = lat, Longitude = lon };
                    }
                }
            }
            return null;
        }
    }

    public static class GeolocationHelper
    {
        private static readonly Regex LatLongRegex = new Regex(@"^([-+]?\d{1,2}([.]\d+)?),\s*([-+]?\d{1,3}([.]\d+)?)$");

        public static bool IsValid(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            return LatLongRegex.IsMatch(input.Trim());
        }

        public static LatLong ToLatLong(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Coordinates cannot be empty.");

            string clean = input.Trim();
            if (!LatLongRegex.IsMatch(clean))
            {
                throw new ArgumentException(
                    string.Format("Invalid coordinates format: '{0}'.\nExpected format: latitude,longitude (e.g. 52.3676,4.9041 for Amsterdam, Netherlands)", input));
            }

            var parts = clean.Split(',');
            return new LatLong
            {
                Latitude = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                Longitude = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture)
            };
        }

        public static LatLong GetFromWindowsLocationApi()
        {
            using (var watcher = new GeoCoordinateWatcher(GeoPositionAccuracy.High))
            {
                watcher.TryStart(false, TimeSpan.FromMilliseconds(2000));
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(100);
                    if (!watcher.Position.Location.IsUnknown)
                    {
                        break;
                    }
                }

                var loc = watcher.Position.Location;
                return new LatLong
                {
                    Latitude = loc.Latitude,
                    Longitude = loc.Longitude
                };
            }
        }

        public static bool RestartDevice(string instanceId)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = string.Format("/restart-device \"{0}\"", instanceId),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit(10000);
                    return proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    internal class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("FakeGPS CLI (Windows 11 Edition)");
            Console.WriteLine("--------------------------------");

            if (args == null || args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "/?")
            {
                PrintHelp();
                return;
            }

            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];

                    if (arg == "-s" || arg == "--set")
                    {
                        if (i + 1 >= args.Length)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("Error: Missing coordinates after " + arg);
                            Console.ResetColor();
                            return;
                        }

                        string coords = args[++i];
                        var latLong = GeolocationHelper.ToLatLong(coords);

                        string instanceId;
                        RegistryHelper.SetLatLong(latLong, out instanceId);

                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("Successfully set coordinates in driver registry:");
                        Console.ResetColor();
                        Console.WriteLine("  Latitude:  {0}", latLong.Latitude.ToString(CultureInfo.InvariantCulture));
                        Console.WriteLine("  Longitude: {0}", latLong.Longitude.ToString(CultureInfo.InvariantCulture));
                        Console.WriteLine("  Device:    {0}", instanceId);

                        Console.WriteLine("Restarting sensor driver to apply new coordinates immediately...");
                        bool restarted = GeolocationHelper.RestartDevice(instanceId);
                        if (restarted)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine("Device restarted successfully. New location is active!");
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine("Note: Could not automatically restart device. You can run 'pnputil /restart-device \"{0}\"' as Administrator.", instanceId);
                            Console.ResetColor();
                        }
                    }
                    else if (arg == "-g" || arg == "--get")
                    {
                        string instanceId;
                        var cfg = RegistryHelper.GetConfiguredLatLong(out instanceId);
                        if (cfg != null)
                        {
                            Console.WriteLine("Driver Configured Location:");
                            Console.WriteLine("  Latitude:  {0}", cfg.Latitude.ToString(CultureInfo.InvariantCulture));
                            Console.WriteLine("  Longitude: {0}", cfg.Longitude.ToString(CultureInfo.InvariantCulture));
                        }

                        Console.WriteLine("\nQuerying Windows Location API...");
                        var apiLoc = GeolocationHelper.GetFromWindowsLocationApi();
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine("Returned from Windows Location API:");
                        Console.ResetColor();
                        Console.WriteLine("  Latitude:  {0}", apiLoc.Latitude.ToString(CultureInfo.InvariantCulture));
                        Console.WriteLine("  Longitude: {0}", apiLoc.Longitude.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (arg == "-r" || arg == "--restart")
                    {
                        string instanceId;
                        RegistryHelper.FindDevicePath(out instanceId);
                        Console.WriteLine("Restarting device {0}...", instanceId);
                        bool restarted = GeolocationHelper.RestartDevice(instanceId);
                        Console.WriteLine(restarted ? "Device restarted successfully!" : "Failed to restart device.");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Unknown argument: {0}", arg);
                        Console.ResetColor();
                        PrintHelp();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: {0}", ex.Message);
                Console.ResetColor();
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage: FakeGPS <command>");
            Console.WriteLine("");
            Console.WriteLine("Options:");
            Console.WriteLine("  -g, --get              Get current location from Windows Location API and driver");
            Console.WriteLine("  -s, --set <lat,long>   Set spoofed coordinates (e.g. -s 52.3676,4.9041)");
            Console.WriteLine("  -r, --restart          Restart the FakeGPS sensor device");
            Console.WriteLine("  -h, --help             Show this help message");
            Console.WriteLine("");
            Console.WriteLine("Examples:");
            Console.WriteLine("  FakeGPS -s 52.3676,4.9041    # Set to Amsterdam, Netherlands");
            Console.WriteLine("  FakeGPS -g                  # Read current location");
        }
    }
}

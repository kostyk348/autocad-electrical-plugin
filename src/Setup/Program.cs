using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace El.Setup
{
    /// <summary>
    /// Установщик надстройки Electrical Tools для AutoCAD.
    ///
    /// Кладёт bundle в папку ApplicationPlugins, откуда AutoCAD загружает плагин сам —
    /// без NETLOAD. Поддерживаемые линейки: 2021–2024 (net48) и 2027 (net10); сборка выбирается
    /// по установленным версиям AutoCAD из реестра. Для 2025/2026 сборки в установщике нет —
    /// об этом честно сообщается в окне.
    ///
    /// Режимы: обычный (окно) и <c>--silent</c> для автотестов/скриптов.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var options = Options.Parse(args);
            try
            {
                if (options.Silent) return RunSilent(options);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var window = new SetupWindow(options))
                {
                    Application.Run(window);
                    return window.ExitCode;
                }
            }
            catch (Exception ex)
            {
                if (options.Silent)
                {
                    Console.Error.WriteLine("ERROR: " + ex.Message);
                    return 2;
                }
                MessageBox.Show(ex.Message, Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }

        private static int RunSilent(Options options)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
            var log = new StringBuilder();
            bool ok = options.Uninstall ? Installer.Uninstall(options.Target, log) : Installer.Install(options.Target, log);
            Console.Write(log.ToString());
            return ok ? 0 : 1;
        }
    }

    internal sealed class Options
    {
        public bool Silent;
        public bool Uninstall;
        public bool AllUsers;
        public string Target;   // переопределение каталога ApplicationPlugins (для тестов)

        public static Options Parse(string[] args)
        {
            var o = new Options();
            if (args == null) return o;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim().ToLowerInvariant().TrimStart('-', '/');
                switch (a)
                {
                    case "silent": case "s": case "quiet": case "q": o.Silent = true; break;
                    case "uninstall": case "remove": case "u": o.Uninstall = true; break;
                    case "allusers": case "all": o.AllUsers = true; break;
                    case "target":
                        if (i + 1 < args.Length) o.Target = args[++i];
                        break;
                }
            }
            return o;
        }
    }

    /// <summary>Установленные версии AutoCAD, найденные в реестре.</summary>
    internal sealed class AcadInstall
    {
        public string Series;      // R24.3, R26.0 …
        public string Product;     // AutoCAD 2024
        public string Location;

        public static List<AcadInstall> Detect()
        {
            var result = new List<AcadInstall>();
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var basePath in new[] { @"SOFTWARE\Autodesk\AutoCAD", @"SOFTWARE\WOW6432Node\Autodesk\AutoCAD" })
                {
                    try
                    {
                        using (var key = root.OpenSubKey(basePath))
                        {
                            if (key == null) continue;
                            foreach (var series in key.GetSubKeyNames())
                            {
                                if (!series.StartsWith("R", StringComparison.OrdinalIgnoreCase)) continue;
                                if (result.Any(r => string.Equals(r.Series, series, StringComparison.OrdinalIgnoreCase))) continue;
                                string product = series;
                                string location = null;
                                try
                                {
                                    using (var sk = key.OpenSubKey(series))
                                    {
                                        if (sk == null) continue;
                                        product = (sk.GetValue("ProductName") as string) ?? series;
                                        location = sk.GetValue("AcadLocation") as string;
                                        if (string.IsNullOrEmpty(location))
                                        {
                                            // у некоторых версий путь лежит на уровень ниже (номер сборки)
                                            var sub = sk.GetSubKeyNames();
                                            for (int i = 0; i < sub.Length && i < 8; i++)
                                            {
                                                using (var inner = sk.OpenSubKey(sub[i]))
                                                {
                                                    location = inner?.GetValue("AcadLocation") as string;
                                                    if (!string.IsNullOrEmpty(location)) break;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (System.Security.SecurityException) { }
                                catch (UnauthorizedAccessException) { }
                                result.Add(new AcadInstall { Series = series, Product = product, Location = location });
                            }
                        }
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Series, b.Series));
            return result;
        }

        /// <summary>Серия реестра → привычное имя версии AutoCAD.</summary>
        public static string DisplayName(string series)
        {
            if (string.IsNullOrEmpty(series)) return "AutoCAD";
            switch (series.Trim().ToUpperInvariant())
            {
                case "R19.0": return "AutoCAD 2013";
                case "R19.1": return "AutoCAD 2014";
                case "R20.0": return "AutoCAD 2015";
                case "R20.1": return "AutoCAD 2016";
                case "R21.0": return "AutoCAD 2017";
                case "R22.0": return "AutoCAD 2018";
                case "R23.0": return "AutoCAD 2019";
                case "R23.1": return "AutoCAD 2020";
                case "R24.0": return "AutoCAD 2021";
                case "R24.1": return "AutoCAD 2022";
                case "R24.2": return "AutoCAD 2023";
                case "R24.3": return "AutoCAD 2024";
                case "R25.0": return "AutoCAD 2025";
                case "R25.1": return "AutoCAD 2026";
                case "R26.0": return "AutoCAD 2027";
                default: return "AutoCAD " + series;
            }
        }

        /// <summary>Линейка → ключ встроенной сборки (null — сборки нет).</summary>
        public static string PayloadFor(string series)
        {
            if (string.IsNullOrEmpty(series)) return null;
            string s = series.Trim().ToUpperInvariant();
            if (s.StartsWith("R24")) return "2024";   // AutoCAD 2021–2024
            if (s.StartsWith("R26")) return "2027";   // AutoCAD 2027 (.NET 10)
            return null;                              // R25.x (2025/2026) — собирается отдельно
        }
    }

    internal static class Installer
    {
        internal const string AppName = "Electrical Tools для AutoCAD";
        internal const string BundleBase = "El.Plugin";
        internal const string UninstallKey = @"Software\ElectricalTools\AutoCAD";
        public const string BundleName = "Electrical Tools";

        internal static string DefaultPluginsDir(bool allUsers)
        {
            string root = allUsers
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "Autodesk", "ApplicationPlugins");
        }

        internal static string[] EmbeddedPayloads()
        {
            var asm = Assembly.GetExecutingAssembly();
            return asm.GetManifestResourceNames()
                      .Where(n => n.StartsWith("El.Plugin.", StringComparison.OrdinalIgnoreCase) &&
                                  n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                      .OrderBy(n => n, StringComparer.Ordinal)
                      .ToArray();
        }

        /// <summary>Какие сборки ставить: по найденным версиям AutoCAD, а если ничего не нашли — все встроенные.</summary>
        internal static List<string> PlanPayloads(List<AcadInstall> detected, StringBuilder log)
        {
            var payloads = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var acad in detected)
            {
                string payload = AcadInstall.PayloadFor(acad.Series);
                if (payload == null)
                {
                    log.AppendLine("  " + AcadInstall.DisplayName(acad.Series) + " [" + acad.Series + "]: сборки в установщике нет — " +
                                   "для AutoCAD 2025/2026 плагин собирается на машине с установленным AutoCAD.");
                    continue;
                }
                if (seen.Add(payload)) payloads.Add(payload);
            }
            if (payloads.Count == 0)
            {
                foreach (var name in EmbeddedPayloads())
                {
                    string payload = name.Replace("El.Plugin.", "").Replace(".dll", "");
                    if (seen.Add(payload)) payloads.Add(payload);
                }
                if (payloads.Count > 0)
                    log.AppendLine("  установленный AutoCAD не найден — ставлю все встроенные сборки: " +
                                   string.Join(", ", payloads));
            }
            return payloads;
        }

        internal static string Manifest(string payload)
        {
            string series = payload == "2024" ? "R24.0|R24.3" : "R26.0|R26.0";
            var parts = series.Split('|');
            string productCode = ProductCodeFor(payload);
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                   "<ApplicationPackage SchemaVersion=\"1.0\"\r\n" +
                   "    AutodeskProduct=\"AutoCAD\"\r\n" +
                   "    Name=\"Electrical Tools (Электроавтоматика)\"\r\n" +
                   "    Description=\"Топология схем, аудит, спецификации AW33, OMNI-история ревизий с авто-слепками.\"\r\n" +
                   "    AppVersion=\"1.13.0\"\r\n" +
                   "    ProductType=\"Application\"\r\n" +
                   "    ProductCode=\"{" + productCode + "}\"\r\n" +
                   "    UpgradeCode=\"{35771725-e194-4a6f-8132-0ea32a5cb5b5}\">\r\n" +
                   "  <Components>\r\n" +
                   "    <RuntimeRequirements OS=\"Win64\" Platform=\"AutoCAD\" SeriesMin=\"" + parts[0] +
                   "\" SeriesMax=\"" + parts[1] + "\" />\r\n" +
                   "    <ComponentEntry AppName=\"El.Plugin\"\r\n" +
                   "        ModuleName=\"./Contents/El.Plugin.dll\"\r\n" +
                   "        AppType=\".NET\"\r\n" +
                   "        LoadOnAutoCADStartup=\"True\"\r\n" +
                   "        LoadOnCommandInvocation=\"False\" />\r\n" +
                   "  </Components>\r\n" +
                   "</ApplicationPackage>\r\n";
        }

        /// <summary>Стабильный ProductCode на сборку (не меняется между запусками установщика).</summary>
        private static string ProductCodeFor(string payload)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("kostyk348/El.Plugin/" + payload));
                return new Guid(hash).ToString("D").ToUpperInvariant();
            }
        }

        internal static bool Install(string targetDir, StringBuilder log)
        {
            string pluginsDir = string.IsNullOrEmpty(targetDir) ? DefaultPluginsDir(false) : targetDir;
            var detected = string.IsNullOrEmpty(targetDir) ? AcadInstall.Detect() : new List<AcadInstall>();
            log.AppendLine("Каталог плагинов: " + pluginsDir);
            log.AppendLine("Найдено AutoCAD: " + (detected.Count == 0 ? "нет" : string.Join(", ", detected.Select(d => AcadInstall.DisplayName(d.Series) + " [" + d.Series + "]"))));

            var payloads = PlanPayloads(detected, log);
            if (payloads.Count == 0)
            {
                log.AppendLine("ОШИБКА: встроенных сборок нет.");
                return false;
            }

            Directory.CreateDirectory(pluginsDir);
            foreach (var payload in payloads)
            {
                string resourceName = "El.Plugin." + payload + ".dll";
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        log.AppendLine("ОШИБКА: нет встроенной сборки " + resourceName);
                        return false;
                    }
                    string bundleDir = Path.Combine(pluginsDir, BundleBase + "." + payload + ".bundle");
                    string contents = Path.Combine(bundleDir, "Contents");
                    // чистая установка: старые файлы (например El.Core.dll от прежних версий) не должны оставаться
                    try { if (Directory.Exists(bundleDir)) Directory.Delete(bundleDir, true); }
                    catch (Exception cleanEx) { log.AppendLine("  предупреждение: не удалось очистить " + bundleDir + ": " + cleanEx.Message); }
                    Directory.CreateDirectory(contents);
                    string dllPath = Path.Combine(contents, "El.Plugin.dll");
                    using (var file = new FileStream(dllPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        stream.CopyTo(file);
                    File.WriteAllText(Path.Combine(bundleDir, "PackageContents.xml"), Manifest(payload), new UTF8Encoding(false));
                    log.AppendLine("  установлено: " + bundleDir + " (" + new FileInfo(dllPath).Length + " байт)");
                }
            }

            WriteUninstallEntry(pluginsDir, log);
            log.AppendLine("Готово. Запустите AutoCAD — плагин загрузится сам, NETLOAD не нужен.");
            return true;
        }

        internal static bool Uninstall(string targetDir, StringBuilder log)
        {
            string pluginsDir = string.IsNullOrEmpty(targetDir) ? DefaultPluginsDir(false) : targetDir;
            int removed = 0;
            if (Directory.Exists(pluginsDir))
            {
                foreach (var dir in Directory.GetDirectories(pluginsDir, BundleBase + ".*.bundle"))
                {
                    try { Directory.Delete(dir, true); removed++; log.AppendLine("  удалено: " + dir); }
                    catch (Exception ex) { log.AppendLine("  не удалось удалить " + dir + ": " + ex.Message); }
                }
            }
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName))
                    if (key != null) Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName, false);
                using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    if (key != null) Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            }
            catch (Exception ex) { log.AppendLine("  реестр: " + ex.Message); }

            log.AppendLine("Удалено плагинов: " + removed);
            return true;
        }

        private static void WriteUninstallEntry(string pluginsDir, StringBuilder log)
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName))
                {
                    if (key == null) return;
                    key.SetValue("DisplayName", AppName);
                    key.SetValue("DisplayVersion", "1.13.0");
                    key.SetValue("Publisher", "kostyk348");
                    key.SetValue("InstallLocation", pluginsDir);
                    key.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                    key.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --silent");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("  запись в «Установка и удаление программ» не создана: " + ex.Message);
            }
        }

        internal static bool IsInstalled(string pluginsDir)
        {
            try
            {
                return Directory.Exists(pluginsDir) &&
                       Directory.GetDirectories(pluginsDir, BundleBase + ".*.bundle").Length > 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void OpenFolder(string path)
        {
            try
            {
                if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception) { }
        }
    }

    /// <summary>Маленькое окно: что нашлось, что ставим, кнопки «Установить»/«Удалить»/«Открыть папку».</summary>
    internal sealed class SetupWindow : Form
    {
        private readonly Options _options;
        private readonly TextBox _log;
        private readonly Button _install;
        private readonly Button _remove;
        private readonly Label _status;

        public int ExitCode { get; private set; }

        public SetupWindow(Options options)
        {
            _options = options;
            Text = "Electrical Tools для AutoCAD — установка";
            Width = 720;
            Height = 470;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            var title = new Label
            {
                Text = "Electrical Tools (Электроавтоматика)",
                Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
                AutoSize = true,
                Left = 16,
                Top = 14
            };
            Controls.Add(title);

            _status = new Label
            {
                Text = "Нажмите «Установить»: плагин попадёт в папку автозагрузки AutoCAD,\nNETLOAD после этого не нужен.",
                AutoSize = true,
                Left = 16,
                Top = 48
            };
            Controls.Add(_status);

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Left = 16,
                Top = 92,
                Width = 670,
                Height = 280,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White
            };
            Controls.Add(_log);

            _install = new Button { Text = "Установить", Left = 16, Top = 386, Width = 150, Height = 32 };
            _install.Click += (s, e) => RunInstall();
            Controls.Add(_install);

            _remove = new Button { Text = "Удалить", Left = 176, Top = 386, Width = 130, Height = 32 };
            _remove.Click += (s, e) => RunUninstall();
            Controls.Add(_remove);

            var open = new Button { Text = "Открыть папку", Left = 316, Top = 386, Width = 140, Height = 32 };
            open.Click += (s, e) => Installer.OpenFolder(Installer.DefaultPluginsDir(false));
            Controls.Add(open);

            var close = new Button { Text = "Закрыть", Left = 546, Top = 386, Width = 140, Height = 32, DialogResult = DialogResult.Cancel };
            Controls.Add(close);
            CancelButton = close;

            Load += (s, e) => ShowDetected();
        }

        private void ShowDetected()
        {
            var detected = AcadInstall.Detect();
            Append("Установленные AutoCAD:");
            if (detected.Count == 0) Append("  не найдены — будут установлены все встроенные сборки");
            foreach (var acad in detected)
            {
                string payload = AcadInstall.PayloadFor(acad.Series);
                Append("  " + AcadInstall.DisplayName(acad.Series) + "  [" + acad.Series + "]  " +
                       (payload == null ? "— сборки в установщике нет (2025/2026)" : "→ сборка " + payload));
            }
            Append("");
            Append("Папка плагинов: " + Installer.DefaultPluginsDir(false));
            Append("Уже установлено: " + (Installer.IsInstalled(Installer.DefaultPluginsDir(false)) ? "да" : "нет"));
        }

        private void RunInstall()
        {
            var log = new StringBuilder();
            bool ok = Installer.Install(_options.Target, log);
            Append("=== Установка ===");
            Append(log.ToString().TrimEnd());
            _install.Enabled = false;
            ExitCode = ok ? 0 : 1;
        }

        private void RunUninstall()
        {
            var log = new StringBuilder();
            bool ok = Installer.Uninstall(_options.Target, log);
            Append("=== Удаление ===");
            Append(log.ToString().TrimEnd());
            ExitCode = ok ? 0 : 1;
        }

        private void Append(string line)
        {
            _log.AppendText(line + Environment.NewLine);
        }
    }
}

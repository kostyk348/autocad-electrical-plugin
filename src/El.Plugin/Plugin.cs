using System;
using System.IO;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using El.Plugin.Ui;

[assembly: ExtensionApplication(typeof(El.Plugin.Plugin))]

namespace El.Plugin
{
    /// <summary>Точка входа плагина: регистрация UI + предложение установки.</summary>
    public class Plugin : IExtensionApplication
    {
        public static readonly string LogPath = Path.Combine(
            Path.GetTempPath(), "el-plugin-errors.log");

        public void Initialize()
        {
            CommandState.LoadLayerFilter();
            try
            {
                Ribbon.Add();
                ContextMenu.Add();
                Palette.Instance = new Palette();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("EL Plugin init: " + ex);
                Log(ex);
            }

            // если загружено вручную (NETLOAD) — предложить автозагрузку
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null && !Installer.IsInstalledLocation())
                {
                    var ed = doc.Editor;
                    var ro = new PromptKeywordOptions(
                        "\n[Электроавтоматика] Установить плагин для автозагрузки (копия в ApplicationPlugins)?");
                    ro.Keywords.Add("Да");
                    ro.Keywords.Add("Нет");
                    ro.Keywords.Default = "Да";
                    ro.AllowNone = true;
                    var res = ed.GetKeywords(ro);
                    if (res.Status == PromptStatus.OK && res.StringResult == "Да")
                        Installer.Install(ed);
                }
            }
            catch (System.Exception ex) { Log(ex); }
        }

        public void Terminate()
        {
            try
            {
                Ribbon.Remove();
                ContextMenu.Remove();
                Palette.Instance?.Dispose();
                Palette.Instance = null;
            }
            catch { }
        }

        /// <summary>Писать ошибки в %TEMP%\el-plugin-errors.log (для диагностики на машине пользователя).</summary>
        public static void Log(System.Exception ex)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n",
                    Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>Диагностика: версия плагина, путь загрузки, версия AutoCAD.</summary>
    public static class DiagCommands
    {
        [CommandMethod("EL-VERSION")]
        public static void ElVersion()
        {
            var ed = DwgAccess.Ed;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var v = asm.GetName().Version;
                var coreAsm = typeof(El.Core.GraphBuilder).Assembly.GetName().Version;
                var corePath = typeof(El.Core.GraphBuilder).Assembly.Location;
                ed.WriteMessage("\n=== Электроавтоматика: диагностика ===");
                ed.WriteMessage($"\nПлагин: {asm.GetName().Name} v{v} (net: {Environment.Version})");
                ed.WriteMessage($"\nПуть: {asm.Location}");
                ed.WriteMessage($"\nEl.Core: v{coreAsm} → {corePath}");
                ed.WriteMessage($"\nУстановлен в ApplicationPlugins: {(Installer.IsInstalledLocation() ? "да" : "нет")}");
                ed.WriteMessage($"\nЛог ошибок: {Plugin.LogPath}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n! EL-VERSION: " + ex.Message);
                Plugin.Log(ex);
            }
        }
    }

    /// <summary>
    /// Самоустановка: копирует плагин в
    /// %APPDATA%\Autodesk\ApplicationPlugins\El.Plugin.2024.bundle\
    /// и генерирует PackageContents.xml — дальше автозагрузка при старте AutoCAD.
    /// </summary>
    public static class Installer
    {
#if NET45
        private const string BundleName = "El.Plugin.2014.bundle";
        private const string SeriesMin = "R19.1";
        private const string SeriesMax = "R19.1";
#else
        private const string BundleName = "El.Plugin.2024.bundle";
        private const string SeriesMin = "R24.0";
        private const string SeriesMax = "R24.3";
#endif
        private static readonly Guid ProductCode = new Guid("066c4090-f8cf-4f6a-957a-56ed496d7f35");
        private static readonly Guid UpgradeCode = new Guid("35771725-e194-4a6f-8132-0ea32a5cb5b5");

        public static string AppPluginsDir()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDir, "Autodesk", "ApplicationPlugins");
        }

        public static string BundleDir() => Path.Combine(AppPluginsDir(), BundleName);

        /// <summary>Загружено ли уже из ApplicationPlugins (штатная установка).</summary>
        public static bool IsInstalledLocation()
        {
            try
            {
                string asmPath = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(asmPath)) return false;
                return asmPath.Replace('/', '\\').StartsWith(BundleDir(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static bool Install(Editor ed)
        {
            try
            {
                string asmPath = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(asmPath))
                {
                    ed.WriteMessage("\n[Электроавтоматика] Не удалось определить путь плагина.");
                    return false;
                }
                string srcDir = Path.GetDirectoryName(asmPath);
                string bundle = BundleDir();
                string contents = Path.Combine(bundle, "Contents");
                Directory.CreateDirectory(contents);

                CopyFile(asmPath, Path.Combine(contents, "El.Plugin.dll"));
                string core = Path.Combine(srcDir, "El.Core.dll");
                if (File.Exists(core))
                    CopyFile(core, Path.Combine(contents, "El.Core.dll"));

                string pkg = Path.Combine(bundle, "PackageContents.xml");
                if (!File.Exists(pkg))
                    File.WriteAllText(pkg, PackageContentsXml(), new UTF8Encoding(true));

                // надёжная автозагрузка через реестр (LoadCtrl=2) — даже если
                // ApplicationPlugins не подхватился
                int regCount = AddRegistryAutoLoad(Path.Combine(contents, "El.Plugin.dll"));

                ed.WriteMessage($"\n[Электроавтоматика] Плагин установлен: {bundle}");
                ed.WriteMessage($"\n[Электроавтоматика] Реестровая автозагрузка: {regCount} записей.");
                ed.WriteMessage("\n[Электроавтоматика] Перезапустите AutoCAD — плагин загрузится автоматически.");
                return true;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n[Электроавтоматика] Ошибка установки: " + ex.Message);
                Plugin.Log(ex);
                return false;
            }
        }

        /// <summary>
        /// Реестровая автозагрузка .NET-приложения (официальный механизм):
        /// HKCU\Software\Autodesk\AutoCAD\R{ver}\{product}\Applications\ElTools
        ///   Loader = путь к DLL, LoaderPath, Managed=1, LoadCtrl=2 (при старте).
        /// Возвращает число созданных записей.
        /// </summary>
        public static int AddRegistryAutoLoad(string loaderDll)
        {
            int count = 0;
            try
            {
                string baseKey = @"SOFTWARE\Autodesk\AutoCAD";
                string loaderPath = Path.GetDirectoryName(loaderDll);
                // ищем все установленные версии AutoCAD (R19.1=2014, R24.x=2024...)
                using (var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(baseKey))
                {
                    if (hklm == null) return 0;
                    foreach (string rVer in hklm.GetSubKeyNames())
                    {
                        if (!rVer.StartsWith("R") || rVer.StartsWith("R1")) continue; // R19.1+ (2014+)
                        using (var rKey = hklm.OpenSubKey(rVer))
                        {
                            if (rKey == null) continue;
                            foreach (string prod in rKey.GetSubKeyNames())
                            {
                                string prodName = "";
                                try
                                {
                                    using (var p = rKey.OpenSubKey(prod))
                                        prodName = p?.GetValue("ProductName") as string ?? "";
                                }
                                catch { }
                                if (!prodName.Contains("AutoCAD")) continue;
                                // создаём зеркало в HKCU
                                string appKey = $@"{baseKey}\{rVer}\{prod}\Applications\ElTools";
                                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(appKey))
                                {
                                    if (k == null) continue;
                                    k.SetValue("Loader", loaderDll);
                                    k.SetValue("LoaderPath", loaderPath);
                                    k.SetValue("Managed", 1, Microsoft.Win32.RegistryValueKind.DWord);
                                    k.SetValue("LoadCtrl", 2, Microsoft.Win32.RegistryValueKind.DWord);
                                    k.SetValue("Description", "Electrical Tools (Электроавтоматика)");
                                }
                                count++;
                            }
                        }
                    }
                }
            }
            catch { }
            return count;
        }

        private static void CopyFile(string src, string dst)
        {
            try { File.Copy(src, dst, true); }
            catch
            {
                // файл может быть заблокирован загруженной сборкой — читаем байты
                byte[] bytes = File.ReadAllBytes(src);
                File.WriteAllBytes(dst, bytes);
            }
        }

        private static string PackageContentsXml()
        {
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<ApplicationPackage SchemaVersion=""1.0""
    AutodeskProduct=""AutoCAD""
    Name=""Electrical Tools (Электроавтоматика)""
    Description=""Топология схем, аудит, спецификации AW33 с кол-вом проводов, провода с XData, OMNI-версии. Ribbon + контекстные меню + палитра.""
    AppVersion=""1.1.0""
    ProductType=""Application""
    ProductCode=""{{{ProductCode}}}""
    UpgradeCode=""{{{UpgradeCode}}}"">
  <Components>
    <RuntimeRequirements OS=""Win64"" Platform=""AutoCAD"" SeriesMin=""{SeriesMin}"" SeriesMax=""{SeriesMax}"" />
    <ComponentEntry AppName=""El.Plugin""
        ModuleName=""./Contents/El.Plugin.dll""
        AppType="".NET""
        LoadOnAutoCADStartup=""True""
        LoadOnCommandInvocation=""False"" />
  </Components>
</ApplicationPackage>
";
        }
    }

    /// <summary>Команды UI/установки.</summary>
    public static class UiCommands
    {
        [CommandMethod("EL-PALETTE")]
        public static void ShowPalette()
        {
            if (Palette.Instance == null) Palette.Instance = new Palette();
            Palette.Instance.Show();
        }

        /// <summary>Явная установка/обновление автозагрузки.</summary>
        [CommandMethod("EL-INSTALL")]
        public static void Install()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            if (Installer.IsInstalledLocation())
            {
                ed.WriteMessage("\n[Электроавтоматика] Плагин уже установлен и загружен из ApplicationPlugins.");
                return;
            }
            Installer.Install(ed);
        }

        /// <summary>Диагностика установки: где бандл, реестр, XML.</summary>
        [CommandMethod("EL-INSTALL-STATUS")]
        public static void InstallStatus()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            try
            {
                ed.WriteMessage("\n=== Электроавтоматика: статус установки ===");
                string asm = Assembly.GetExecutingAssembly().Location;
                ed.WriteMessage($"\nЗагружен из: {asm}");
                ed.WriteMessage($"\nApplicationPlugins (HKCU): {Installer.AppPluginsDir()}");

                var roam = Path.Combine(Installer.AppPluginsDir(), "El.Plugin.2024.bundle");
                var roam14 = Path.Combine(Installer.AppPluginsDir(), "El.Plugin.2014.bundle");
                ed.WriteMessage($"\n[2024] {roam}: {(Directory.Exists(roam) ? "ЕСТЬ" : "нет")}");
                ed.WriteMessage($"\n[2014] {roam14}: {(Directory.Exists(roam14) ? "ЕСТЬ" : "нет")}");
                if (Directory.Exists(roam))
                {
                    string pkg = Path.Combine(roam, "PackageContents.xml");
                    ed.WriteMessage($"\n  PackageContents.xml: {(File.Exists(pkg) ? "ЕСТЬ" : "НЕТ!")}");
                    string dll = Path.Combine(roam, "Contents", "El.Plugin.dll");
                    string core = Path.Combine(roam, "Contents", "El.Core.dll");
                    ed.WriteMessage($"\n  El.Plugin.dll: {(File.Exists(dll) ? "ЕСТЬ" : "НЕТ!")}");
                    ed.WriteMessage($"\n  El.Core.dll: {(File.Exists(core) ? "ЕСТЬ" : "НЕТ!")}");
                }

                // реестровые записи
                ed.WriteMessage("\nРеестр (автозагрузка):");
                using (var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Autodesk\AutoCAD"))
                {
                    if (hklm == null) { ed.WriteMessage("\n  AutoCAD в реестре не найден"); }
                    else
                    {
                        foreach (string rVer in hklm.GetSubKeyNames())
                        {
                            if (!rVer.StartsWith("R")) continue;
                            using (var rKey = hklm.OpenSubKey(rVer))
                            {
                                foreach (string prod in rKey.GetSubKeyNames())
                                {
                                    string app = $@"HKCU\SOFTWARE\Autodesk\AutoCAD\{rVer}\{prod}\Applications\ElTools";
                                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Autodesk\AutoCAD\{rVer}\{prod}\Applications\ElTools"))
                                    {
                                        string loader = k?.GetValue("Loader") as string ?? "";
                                        ed.WriteMessage($"\n  {rVer}\\{prod}: {(k != null ? "ЕСТЬ → " + loader : "нет")}");
                                    }
                                }
                            }
                        }
                    }
                }
                ed.WriteMessage($"\nЛог ошибок: {Plugin.LogPath}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n! EL-INSTALL-STATUS: " + ex.Message);
                Plugin.Log(ex);
            }
        }
    }
}

using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using El.Plugin.Ui;

[assembly: ExtensionApplication(typeof(El.Plugin.Plugin))]

namespace El.Plugin
{
    /// <summary>Точка входа плагина: регистрация UI при загрузке.</summary>
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                Ribbon.Add();
                ContextMenu.Add();
                Palette.Instance = new Palette();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("EL Plugin init: " + ex);
            }
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
    }

    /// <summary>Открыть палитру цепей вручную.</summary>
    public static class UiCommands
    {
        [CommandMethod("EL-PALETTE")]
        public static void ShowPalette()
        {
            if (Palette.Instance == null) Palette.Instance = new Palette();
            Palette.Instance.Show();
        }
    }
}

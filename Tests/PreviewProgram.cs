using System;
using System.IO;
using System.Windows;

namespace QuickLook.Plugin.FolderViewer.PreviewHarness
{
    internal static class PreviewProgram
    {
        [STAThread]
        private static int Main()
        {
            try
            {
                var path = Environment.GetEnvironmentVariable("FOLDERVIEWER_PREVIEW_PATH");
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    path = Environment.SystemDirectory;

                var application = new Application();
                var panel = new FolderInfoPanel(path);
                var window = new Window
                {
                    Title = "FolderViewer Preview - " + path,
                    Width = 900,
                    Height = 500,
                    MinWidth = 720,
                    MinHeight = 320,
                    Content = panel,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                window.Closed += (sender, eventArgs) => panel.Dispose();
                return application.Run(window);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.GetType().FullName);
                Console.Error.WriteLine(exception.Message);
                if (exception.InnerException != null)
                {
                    Console.Error.WriteLine(exception.InnerException.GetType().FullName);
                    Console.Error.WriteLine(exception.InnerException.Message);
                }
                return 1;
            }
        }
    }
}

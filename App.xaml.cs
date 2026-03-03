using System;
using System.Windows;

namespace FbmodDecompiler
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Bootstrapper.EnsureRuntimeFiles();
            base.OnStartup(e);
            try { AppState.Audio.InitAndPlay(); } catch { }
            ShowMainWindow();
        }

        public static void ShowMainWindow()
        {
            try
            {
                var main = new MainWindow();
                Application.Current.MainWindow = main;
                main.Show();
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show("Failed to open main window.\n\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }

                try { Application.Current.Shutdown(); } catch { }
            }
        }
    }
}

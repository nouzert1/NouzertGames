using System.Windows;

namespace NouzertGames
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Global exception handling
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            System.AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var logger = new Services.LoggerService();
            logger.Error($"[CRITICAL UI ERROR]: {e.Exception.Message}", e.Exception);
            
            // Impede que o aplicativo feche devido a erros na thread de UI
            e.Handled = true; 
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            var logger = new Services.LoggerService();
            if (e.ExceptionObject is Exception ex)
            {
                logger.Error($"[CRITICAL DOMAIN ERROR]: {ex.Message}", ex);
            }
            else
            {
                logger.Error($"[CRITICAL DOMAIN ERROR]: {e.ExceptionObject}");
            }
        }
    }
}
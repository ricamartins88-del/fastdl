using System.Windows;

namespace FastDl;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Uma instância só: duas janelas disputariam os mesmos arquivos de estado.
        _mutex = new Mutex(true, @"Local\FastDl.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("O FastDl já está aberto.", "FastDl", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show($"Ocorreu um erro inesperado:\n\n{args.Exception.Message}\n\nDetalhes em {Storage.Dir}\\crash.log",
                "FastDl", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Storage.Dir);
            File.AppendAllText(Path.Combine(Storage.Dir, "crash.log"), $"[{DateTime.Now:O}]\n{ex}\n\n");
        }
        catch { /* ignora */ }
    }
}

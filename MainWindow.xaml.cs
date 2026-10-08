using System.ComponentModel;
using System.Windows;

namespace FastDl;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _shutdownStarted;
    private bool _shutdownDone;
    private string? _lastClipboard;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Activated += OnActivated;
    }

    /// <summary>Ao voltar para a janela, se a área de transferência tem um link, já preenche o campo.</summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_vm.NewUrl)) return;

        try
        {
            if (!Clipboard.ContainsText()) return;

            string text = Clipboard.GetText().Trim();
            if (text == _lastClipboard || text.Any(char.IsWhiteSpace)) return;

            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                _lastClipboard = text;
                _vm.NewUrl = text;
            }
        }
        catch
        {
            // a área de transferência pode estar em uso por outro programa
        }
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        bool hasText = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);
        e.Effects = hasText ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        string? text = e.Data.GetData(DataFormats.UnicodeText) as string
                       ?? e.Data.GetData(DataFormats.Text) as string;

        if (!string.IsNullOrWhiteSpace(text))
            _vm.AddFromText(text);
    }

    /// <summary>Fechar = pausar tudo com segurança e só então sair.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shutdownDone) return;

        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;

        IsEnabled = false;
        try { await _vm.ShutdownAsync(); }
        catch { /* mesmo com erro, o app precisa fechar */ }

        _shutdownDone = true;
        Close();
    }
}

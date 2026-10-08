using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace FastDl;

public enum DownloadStatus { Queued, Downloading, Paused, Completed, Failed }

/// <summary>Um download na lista: guarda o estado exibido e controla o seu próprio motor.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private DownloadStatus _status;
    private string _fileName;
    private long _done;
    private long _total;
    private double _speed;
    private TimeSpan? _eta;
    private int _activeConnections;
    private string _message = "";
    private string? _finalPath;

    private CancellationTokenSource? _cts;
    private bool _pausing;
    private bool _discarded;
    private bool _deleteFilesOnDiscard;

    public Guid Id { get; }
    public string Url { get; }
    public string OutputDir { get; }
    public int Connections { get; }

    /// <summary>Tarefa em andamento (usada para aguardar o encerramento ao fechar o app).</summary>
    public Task? RunTask { get; private set; }

    /// <summary>Disparado quando o status muda (a fila e o disco são atualizados por quem escuta).</summary>
    public event Action<DownloadItemViewModel>? Changed;
    public event Action<DownloadItemViewModel, bool>? RemoveRequested;

    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand RemoveWithFilesCommand { get; }

    // ------------------------------------------------------------------
    // Construção
    // ------------------------------------------------------------------

    public static DownloadItemViewModel Create(string url, string outputDir, int connections) =>
        new(new ItemData
        {
            Id = Guid.NewGuid(),
            Url = url,
            OutputDir = outputDir,
            FileName = GuessFileName(url),
            Connections = connections,
            Status = DownloadStatus.Queued,
        });

    public DownloadItemViewModel(ItemData d)
    {
        Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id;
        Url = d.Url;
        OutputDir = d.OutputDir;
        Connections = d.Connections;
        _fileName = string.IsNullOrWhiteSpace(d.FileName) ? GuessFileName(d.Url) : d.FileName;
        _done = d.Done;
        _total = d.Total;
        _finalPath = d.FinalPath;

        // Ao reabrir o app nada começa sozinho: o que estava ativo ou na fila fica pausado.
        _status = d.Status is DownloadStatus.Downloading or DownloadStatus.Queued
            ? DownloadStatus.Paused
            : d.Status;

        PauseCommand = new RelayCommand(Pause);
        ResumeCommand = new RelayCommand(Resume);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        OpenFileCommand = new RelayCommand(OpenFile);
        CopyUrlCommand = new RelayCommand(CopyUrl);
        RemoveCommand = new RelayCommand(() => RemoveRequested?.Invoke(this, false));
        RemoveWithFilesCommand = new RelayCommand(() => RemoveRequested?.Invoke(this, true));
    }

    public ItemData ToData() => new()
    {
        Id = Id,
        Url = Url,
        OutputDir = OutputDir,
        FileName = FileName,
        Connections = Connections,
        Status = Status,
        Done = Done,
        Total = Total,
        FinalPath = FinalPath,
    };

    private static string GuessFileName(string url)
    {
        try
        {
            string name = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
            return string.IsNullOrWhiteSpace(name) ? "download" : name;
        }
        catch
        {
            return "download";
        }
    }

    // ------------------------------------------------------------------
    // Propriedades exibidas na tela
    // ------------------------------------------------------------------

    public DownloadStatus Status => _status;
    public string FileName { get => _fileName; private set => Set(ref _fileName, value); }
    public long Done { get => _done; private set => _done = value; }
    public long Total { get => _total; private set => _total = value; }
    public double Speed => _speed;
    public string? FinalPath => _finalPath;

    public double Progress =>
        _status == DownloadStatus.Completed ? 100 :
        _total > 0 ? Math.Min(100.0, _done * 100.0 / _total) : 0;

    public bool Indeterminate => _status == DownloadStatus.Downloading && _total <= 0;
    public bool CanPause => _status is DownloadStatus.Queued or DownloadStatus.Downloading;
    public bool CanResume => _status is DownloadStatus.Paused or DownloadStatus.Failed;
    public bool IsCompleted => _status == DownloadStatus.Completed;
    public bool IsFailed => _status == DownloadStatus.Failed;
    public bool ShowProgress => _status != DownloadStatus.Completed;

    public string SizeText =>
        _total > 0 ? $"{Fmt.Bytes(_done)} / {Fmt.Bytes(_total)}" :
        _done > 0 ? Fmt.Bytes(_done) : "";

    public string Details
    {
        get
        {
            switch (_status)
            {
                case DownloadStatus.Queued:
                    return "Na fila";

                case DownloadStatus.Downloading:
                    if (_pausing) return "Pausando...";
                    if (_total <= 0 && _done == 0)
                        return string.IsNullOrEmpty(_message) ? "Iniciando..." : _message;

                    var parts = new List<string>();
                    if (_total > 0) parts.Add($"{Progress:0.0}%");
                    if (_speed > 0) parts.Add($"{Fmt.Bytes(_speed)}/s");
                    if (_eta is TimeSpan eta) parts.Add($"faltam {Fmt.Time(eta)}");
                    if (_activeConnections > 0)
                        parts.Add(_activeConnections == 1 ? "1 conexão" : $"{_activeConnections} conexões");
                    return "Baixando • " + string.Join(" • ", parts);

                case DownloadStatus.Paused:
                    return _total > 0 ? $"Pausado • {Progress:0.0}%" : "Pausado";

                case DownloadStatus.Completed:
                    return $"Concluído • {_finalPath ?? OutputDir}";

                case DownloadStatus.Failed:
                    return string.IsNullOrWhiteSpace(_message) ? "Falhou" : $"Falhou • {_message}";

                default:
                    return "";
            }
        }
    }

    private void RefreshAll()
    {
        Raise(nameof(Status));
        Raise(nameof(Done));
        Raise(nameof(Total));
        Raise(nameof(Speed));
        Raise(nameof(FinalPath));
        Raise(nameof(Progress));
        Raise(nameof(Indeterminate));
        Raise(nameof(CanPause));
        Raise(nameof(CanResume));
        Raise(nameof(IsCompleted));
        Raise(nameof(IsFailed));
        Raise(nameof(ShowProgress));
        Raise(nameof(SizeText));
        Raise(nameof(Details));
    }

    private void SetStatus(DownloadStatus status)
    {
        _status = status;
        _pausing = false;
        RefreshAll();
        Changed?.Invoke(this);
    }

    private static void UI(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    // ------------------------------------------------------------------
    // Controle do download
    // ------------------------------------------------------------------

    /// <summary>Inicia (ou retoma) o download. Chamado pela fila quando há vaga.</summary>
    public void Start()
    {
        if (_status == DownloadStatus.Downloading) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _message = "";
        _speed = 0;
        _eta = null;
        _activeConnections = 0;
        SetStatus(DownloadStatus.Downloading);

        var engine = new DownloadEngine(SharedHttp.Client);

        // O motor dispara eventos em threads do pool: tudo que mexe na tela passa pela UI.
        engine.Probed += p => UI(() =>
        {
            if (_status != DownloadStatus.Downloading) return;
            FileName = p.FileName;
            if (p.TotalSize is long t) _total = t;
            RefreshAll();
        });

        engine.Progress += p => UI(() =>
        {
            if (_status != DownloadStatus.Downloading) return;
            _done = p.Done;
            if (p.Total is long t) _total = t;
            _speed = p.BytesPerSecond;
            _eta = p.Eta;
            _activeConnections = p.Connections;
            RefreshAll();
        });

        engine.Log += m => UI(() =>
        {
            if (_status != DownloadStatus.Downloading) return;
            _message = m;
            Raise(nameof(Details));
        });

        RunTask = RunAsync(engine, cts);
    }

    private async Task RunAsync(DownloadEngine engine, CancellationTokenSource cts)
    {
        DownloadOutcome outcome;
        try
        {
            outcome = await Task.Run(() => engine.RunAsync(Url, OutputDir, Connections, cts.Token));
        }
        catch (OperationCanceledException)
        {
            outcome = DownloadOutcome.Paused;
        }
        catch (Exception ex)
        {
            outcome = DownloadOutcome.Failed;
            _message = ex.Message;
        }

        // Daqui em diante estamos de volta na thread da UI.
        cts.Dispose();
        if (ReferenceEquals(_cts, cts)) _cts = null;
        _speed = 0;
        _eta = null;
        _activeConnections = 0;

        if (_discarded)
        {
            CleanupFiles();
            return;
        }

        switch (outcome)
        {
            case DownloadOutcome.Completed:
                _finalPath = engine.FinalPath;
                if (_total > 0) _done = _total;
                SetStatus(DownloadStatus.Completed);
                break;

            case DownloadOutcome.Paused:
                SetStatus(DownloadStatus.Paused);
                break;

            default:
                SetStatus(DownloadStatus.Failed);
                break;
        }
    }

    public void Pause()
    {
        if (_status == DownloadStatus.Queued)
        {
            SetStatus(DownloadStatus.Paused);
        }
        else if (_status == DownloadStatus.Downloading && !_pausing)
        {
            _pausing = true;
            Raise(nameof(Details));
            _cts?.Cancel(); // o motor salva o estado e devolve "Paused"
        }
    }

    public void Resume()
    {
        if (_status is DownloadStatus.Paused or DownloadStatus.Failed)
            SetStatus(DownloadStatus.Queued); // a fila inicia quando houver vaga
    }

    /// <summary>Pede para parar e devolve a tarefa, para o app poder aguardar ao fechar.</summary>
    public Task StopAndWait()
    {
        if (_status == DownloadStatus.Downloading && !_pausing)
        {
            _pausing = true;
            _cts?.Cancel();
        }
        return RunTask ?? Task.CompletedTask;
    }

    /// <summary>Item removido da lista: para o download e, se pedido, apaga os arquivos.</summary>
    public void Discard(bool deleteFiles)
    {
        _discarded = true;
        _deleteFilesOnDiscard = deleteFiles;

        if (_status == DownloadStatus.Downloading)
            _cts?.Cancel(); // a limpeza acontece quando o motor terminar de parar
        else
            CleanupFiles();
    }

    private void CleanupFiles()
    {
        if (!_deleteFilesOnDiscard) return;

        TryDelete(Path.Combine(OutputDir, FileName + ".part"));
        TryDelete(Path.Combine(OutputDir, FileName + ".fdl"));
        if (_status == DownloadStatus.Completed && _finalPath != null)
            TryDelete(_finalPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignora */ }
    }

    // ------------------------------------------------------------------
    // Ações de menu
    // ------------------------------------------------------------------

    private void OpenFolder()
    {
        try
        {
            if (_status == DownloadStatus.Completed && _finalPath != null && File.Exists(_finalPath))
                Process.Start("explorer.exe", $"/select,\"{_finalPath}\"");
            else if (Directory.Exists(OutputDir))
                Process.Start(new ProcessStartInfo(OutputDir) { UseShellExecute = true });
        }
        catch { /* ignora */ }
    }

    private void OpenFile()
    {
        try
        {
            if (_finalPath != null && File.Exists(_finalPath))
                Process.Start(new ProcessStartInfo(_finalPath) { UseShellExecute = true });
            else
                MessageBox.Show("O arquivo não foi encontrado. Ele pode ter sido movido ou apagado.",
                    "FastDl", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { /* ignora */ }
    }

    private void CopyUrl()
    {
        try { Clipboard.SetText(Url); } catch { /* área de transferência ocupada */ }
    }
}

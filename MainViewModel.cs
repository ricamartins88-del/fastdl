using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FastDl;

public sealed class MainViewModel : ObservableObject
{
    private const string ItemsFile = "downloads.json";

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _timer;
    private bool _pumping;
    private bool _shuttingDown;
    private int _ticks;

    private string _newUrl = "";
    private string _errorText = "";
    private string _summary = "Nenhum download ativo";

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

    public int[] ConnectionOptions { get; } = { 1, 2, 4, 6, 8, 12, 16, 24, 32 };
    public int[] SimultaneousOptions { get; } = { 1, 2, 3, 4, 5, 6, 8 };

    public ICommand AddCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand ClearCompletedCommand { get; }

    public MainViewModel()
    {
        _settings.ConnectionsPerDownload = Closest(ConnectionOptions, _settings.ConnectionsPerDownload);
        _settings.MaxSimultaneous = Closest(SimultaneousOptions, _settings.MaxSimultaneous);
        if (string.IsNullOrWhiteSpace(_settings.DownloadFolder))
            _settings.DownloadFolder = new AppSettings().DownloadFolder;

        AddCommand = new RelayCommand(() => AddFromText(NewUrl));
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        PauseAllCommand = new RelayCommand(() => { foreach (var i in Items.ToList()) i.Pause(); });
        ResumeAllCommand = new RelayCommand(() => { foreach (var i in Items.ToList()) i.Resume(); });
        ClearCompletedCommand = new RelayCommand(ClearCompleted);

        var saved = Storage.Load<List<ItemData>>(ItemsFile);
        if (saved != null)
        {
            foreach (var data in saved)
            {
                if (string.IsNullOrWhiteSpace(data.Url)) continue;
                var item = new DownloadItemViewModel(data);
                Attach(item);
                Items.Add(item);
            }
        }

        Items.CollectionChanged += (_, _) => Raise(nameof(IsEmpty));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    // ------------------------------------------------------------------
    // Propriedades ligadas à tela
    // ------------------------------------------------------------------

    public string NewUrl
    {
        get => _newUrl;
        set => Set(ref _newUrl, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set { if (Set(ref _errorText, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);
    public bool IsEmpty => Items.Count == 0;

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string DownloadFolder
    {
        get => _settings.DownloadFolder;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _settings.DownloadFolder) return;
            _settings.DownloadFolder = value.Trim();
            _settings.Save();
            Raise();
        }
    }

    public int ConnectionsPerDownload
    {
        get => _settings.ConnectionsPerDownload;
        set
        {
            if (value == _settings.ConnectionsPerDownload) return;
            _settings.ConnectionsPerDownload = value;
            _settings.Save();
            Raise();
        }
    }

    public int MaxSimultaneous
    {
        get => _settings.MaxSimultaneous;
        set
        {
            if (value == _settings.MaxSimultaneous) return;
            _settings.MaxSimultaneous = value;
            _settings.Save();
            Raise();
            Pump(); // se aumentou o limite, a fila avança na hora
        }
    }

    // ------------------------------------------------------------------
    // Adicionar downloads
    // ------------------------------------------------------------------

    public void AddFromText(string? text)
    {
        ErrorText = "";

        var tokens = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            ErrorText = "Cole um link primeiro.";
            return;
        }

        int added = 0, invalid = 0, duplicated = 0;
        foreach (string token in tokens)
        {
            if (!Uri.TryCreate(token, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                invalid++;
                continue;
            }

            if (Items.Any(i => i.Url == token && i.Status != DownloadStatus.Completed))
            {
                duplicated++;
                continue;
            }

            try { Directory.CreateDirectory(DownloadFolder); }
            catch
            {
                ErrorText = "Não foi possível usar a pasta de destino. Escolha outra.";
                return;
            }

            var item = DownloadItemViewModel.Create(token, DownloadFolder, ConnectionsPerDownload);
            Attach(item);
            Items.Insert(0, item); // os mais novos aparecem no topo
            added++;
        }

        if (added > 0) NewUrl = "";

        if (invalid > 0 && added == 0) ErrorText = "Link inválido. Use um endereço http:// ou https://.";
        else if (invalid > 0) ErrorText = $"{invalid} link(s) inválido(s) foram ignorados.";
        else if (duplicated > 0 && added == 0) ErrorText = "Esse link já está na lista.";

        Pump();
        Save();
    }

    private void Attach(DownloadItemViewModel item)
    {
        item.Changed += _ => { Pump(); Save(); };
        item.RemoveRequested += OnRemoveRequested;
    }

    private void OnRemoveRequested(DownloadItemViewModel item, bool deleteFiles)
    {
        if (deleteFiles)
        {
            var answer = MessageBox.Show(
                $"Apagar também os arquivos de \"{item.FileName}\"?\n\nIsso não pode ser desfeito.",
                "FastDl", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        Items.Remove(item);
        item.Discard(deleteFiles);
        Pump();
        Save();
    }

    private void ClearCompleted()
    {
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Completed).ToList())
            Items.Remove(item);
        Save();
    }

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Escolha a pasta de destino",
            InitialDirectory = Directory.Exists(DownloadFolder) ? DownloadFolder : null,
        };

        if (dialog.ShowDialog() == true)
            DownloadFolder = dialog.FolderName;
    }

    // ------------------------------------------------------------------
    // Fila
    // ------------------------------------------------------------------

    /// <summary>Inicia os itens "Na fila" respeitando o limite de downloads simultâneos.</summary>
    private void Pump()
    {
        if (_pumping || _shuttingDown) return;
        _pumping = true;
        try
        {
            int running = Items.Count(i => i.Status == DownloadStatus.Downloading);

            // Ordem de chegada: o mais antigo da fila começa primeiro (a lista tem os novos no topo).
            var waiting = Items.AsEnumerable().Reverse()
                .Where(i => i.Status == DownloadStatus.Queued)
                .ToList();

            foreach (var item in waiting)
            {
                if (running >= MaxSimultaneous) break;
                item.Start();
                running++;
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private void OnTick()
    {
        var active = Items.Where(i => i.Status == DownloadStatus.Downloading).ToList();
        int queued = Items.Count(i => i.Status == DownloadStatus.Queued);

        if (active.Count == 0 && queued == 0)
        {
            Summary = "Nenhum download ativo";
        }
        else
        {
            double speed = active.Sum(i => i.Speed);
            Summary = $"{active.Count} baixando • {Fmt.Bytes(speed)}/s";
            if (queued > 0) Summary += $" • {queued} na fila";
        }

        // Salva o progresso de tempos em tempos (o estado detalhado do motor já é salvo a cada 2 s).
        if (active.Count > 0 && ++_ticks % 3 == 0)
            Save();
    }

    // ------------------------------------------------------------------
    // Persistência e encerramento
    // ------------------------------------------------------------------

    private void Save() =>
        Storage.Save(ItemsFile, Items.Select(i => i.ToData()).ToList());

    /// <summary>Pausa tudo com segurança (o motor salva o estado) antes de fechar o app.</summary>
    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _timer.Stop();

        var tasks = Items.ToList().Select(i => i.StopAndWait()).ToList();
        if (tasks.Count > 0)
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(8)));

        Save();
    }

    private static int Closest(int[] options, int value) =>
        options.OrderBy(o => Math.Abs(o - value)).First();
}

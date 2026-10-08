using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FastDl;

/// <summary>
/// Motor de download: várias conexões (segmentos), divisão dinâmica,
/// pausa/retomada com estado salvo em disco e tentativas automáticas.
/// Não depende de interface: a UI (console hoje, WPF amanhã) só escuta os eventos.
/// </summary>
public sealed class DownloadEngine
{
    private const int BufferSize = 128 * 1024;
    private const long MinSegmentBytes = 1024 * 1024;       // não cria segmentos menores que 1 MB
    private const long MinSplitBytes = 2 * 1024 * 1024;     // só divide um segmento com >= 2 MB restantes
    private const int MaxRetries = 10;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly HttpClient _http;
    private readonly object _lock = new();   // protege _state e os campos dos segmentos
    private DownloadState _state = new();
    private Exception? _fatal;

    public event Action<string>? Log;
    public event Action<ProgressInfo>? Progress;
    public event Action<ProbeResult>? Probed;

    /// <summary>Caminho do arquivo final, preenchido quando o download termina.</summary>
    public string? FinalPath { get; private set; }

    public DownloadEngine(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Cria o HttpClient que deve ser compartilhado por todos os downloads do app.</summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            // Sem descompressão automática: com Range, os bytes têm que ser exatamente os do arquivo.
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = int.MaxValue,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan // timeouts são controlados por leitura (IoTimeout)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) FastDl/0.2");
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        return http;
    }

    // ------------------------------------------------------------------
    // Ponto de entrada
    // ------------------------------------------------------------------

    public async Task<DownloadOutcome> RunAsync(string url, string outputDir, int connections, CancellationToken ct)
    {
        connections = Math.Clamp(connections, 1, 32);
        Directory.CreateDirectory(outputDir);
        FinalPath = null;

        Log?.Invoke("Consultando o servidor...");
        ProbeResult probe;
        try
        {
            probe = await ProbeAsync(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DownloadOutcome.Paused;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Falha ao consultar o servidor: {ex.Message}");
            return DownloadOutcome.Failed;
        }

        Probed?.Invoke(probe);
        Log?.Invoke($"Arquivo: {probe.FileName}");
        Log?.Invoke(probe.TotalSize is long size ? $"Tamanho: {Fmt.Bytes(size)}" : "Tamanho: desconhecido");
        Log?.Invoke(probe.SupportsRanges
            ? "Servidor aceita Range: segmentos e retomada disponíveis."
            : "Servidor NÃO aceita Range: conexão única, sem retomada.");

        // Sem Range (ou tamanho desconhecido): cai para conexão única.
        if (!probe.SupportsRanges || probe.TotalSize is null or 0)
            return await DownloadSingleAsync(probe, outputDir, ct);

        long total = probe.TotalSize.Value;
        string partPath = Path.Combine(outputDir, probe.FileName + ".part");
        string statePath = Path.Combine(outputDir, probe.FileName + ".fdl");

        var saved = LoadState(statePath);
        if (saved != null && File.Exists(partPath) && IsResumable(saved, probe, total))
        {
            saved.FinalUrl = probe.FinalUrl; // renova o link (pode ter token novo)
            _state = saved;
            long already = saved.Segments.Sum(s => s.Downloaded);
            Log?.Invoke($"Retomando: {Fmt.Bytes(already)} já baixados.");
        }
        else
        {
            if (saved != null)
                Log?.Invoke("O arquivo no servidor mudou ou o estado salvo é inválido. Recomeçando do zero.");
            TryDelete(partPath);
            _state = NewState(url, probe, total, connections);
        }

        _fatal = null;
        var outcome = await DownloadSegmentsAsync(partPath, statePath, total, connections, ct);
        if (outcome != DownloadOutcome.Completed)
            return outcome;

        if (new FileInfo(partPath).Length != total)
        {
            Log?.Invoke("O tamanho final do arquivo não confere. Download marcado como falho.");
            return DownloadOutcome.Failed;
        }

        FinalPath = Finish(partPath, outputDir, probe.FileName, statePath);
        return DownloadOutcome.Completed;
    }

    // ------------------------------------------------------------------
    // Consulta inicial ao servidor
    // ------------------------------------------------------------------

    private async Task<ProbeResult> ProbeAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        // GET com Range 0-0 funciona mesmo em servidores que rejeitam HEAD.
        var resp = await _http.SendAsync(NewRequest(url, 0, 0), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) // arquivo vazio
        {
            resp.Dispose();
            resp = await _http.SendAsync(NewRequest(url, null, null), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();

            bool ranges = false;
            long? total = null;

            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                if (resp.Content.Headers.ContentRange?.Length is long len)
                {
                    total = len;
                    ranges = true;
                }
            }
            else
            {
                total = resp.Content.Headers.ContentLength;
            }

            string finalUrl = resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
            return new ProbeResult(
                finalUrl,
                ResolveFileName(resp, finalUrl),
                total,
                ranges,
                resp.Headers.ETag?.ToString(),
                resp.Content.Headers.LastModified?.ToString("O"));
        }
    }

    private static string ResolveFileName(HttpResponseMessage resp, string finalUrl)
    {
        var cd = resp.Content.Headers.ContentDisposition;
        string? name = cd?.FileNameStar ?? cd?.FileName;
        name = name?.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(name))
        {
            try { name = Uri.UnescapeDataString(Path.GetFileName(new Uri(finalUrl).AbsolutePath)); }
            catch { name = null; }
        }

        if (string.IsNullOrWhiteSpace(name)) name = "download";

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        name = name.Trim().Trim('.');
        return name.Length == 0 ? "download" : name;
    }

    // ------------------------------------------------------------------
    // Estado (pausa / retomada)
    // ------------------------------------------------------------------

    private static DownloadState NewState(string url, ProbeResult probe, long total, int connections)
    {
        var state = new DownloadState
        {
            Url = url,
            FinalUrl = probe.FinalUrl,
            FileName = probe.FileName,
            TotalSize = total,
            ETag = probe.ETag,
            LastModified = probe.LastModified,
        };

        int count = (int)Math.Clamp(total / MinSegmentBytes, 1, connections);
        long chunk = total / count;
        for (int i = 0; i < count; i++)
        {
            long start = i * chunk;
            long end = i == count - 1 ? total - 1 : start + chunk - 1;
            state.Segments.Add(new Segment { Start = start, End = end });
        }
        return state;
    }

    private static DownloadState? LoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Só retoma se o arquivo no servidor continua sendo o mesmo.</summary>
    private static bool IsResumable(DownloadState saved, ProbeResult probe, long total)
    {
        if (saved.TotalSize != total) return false;
        if (saved.ETag != null && probe.ETag != null && saved.ETag != probe.ETag) return false;
        if (saved.LastModified != null && probe.LastModified != null && saved.LastModified != probe.LastModified) return false;
        if (saved.Segments.Count == 0) return false;

        return saved.Segments.All(s =>
            s.Start >= 0 && s.End < total && s.Start <= s.End &&
            s.Downloaded >= 0 && s.Downloaded <= s.End - s.Start + 1);
    }

    private void SaveState(SafeFileHandle handle, string statePath)
    {
        // Garante que os bytes que o estado diz ter baixado estão mesmo no disco.
        RandomAccess.FlushToDisk(handle);

        string json;
        lock (_lock) json = JsonSerializer.Serialize(_state, JsonOpts);

        string tmp = statePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, statePath, overwrite: true); // troca atômica: nunca fica meio escrito
    }

    // ------------------------------------------------------------------
    // Download em segmentos
    // ------------------------------------------------------------------

    private async Task<DownloadOutcome> DownloadSegmentsAsync(
        string partPath, string statePath, long total, int connections, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using (var handle = File.OpenHandle(
            partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous))
        {
            if (RandomAccess.GetLength(handle) != total)
                RandomAccess.SetLength(handle, total);

            var workers = Enumerable.Range(0, connections)
                .Select(_ => Task.Run(() => WorkerAsync(handle, stop)))
                .ToArray();

            await ReportLoopAsync(
                Task.WhenAll(workers),
                SumDownloaded,
                total,
                ActiveCount,
                () => { try { SaveState(handle, statePath); } catch (IOException) { /* tenta de novo no próximo ciclo */ } });

            SaveState(handle, statePath); // estado final (pausa, falha ou conclusão)
        }

        if (_fatal != null)
        {
            Log?.Invoke($"Falha: {_fatal.Message}");
            Log?.Invoke("O progresso foi salvo. Rode o mesmo comando para tentar retomar.");
            return DownloadOutcome.Failed;
        }

        if (ct.IsCancellationRequested)
            return DownloadOutcome.Paused;

        lock (_lock)
        {
            if (_state.Segments.Any(s => s.Remaining > 0))
                return DownloadOutcome.Failed;
        }

        return DownloadOutcome.Completed;
    }

    private async Task WorkerAsync(SafeFileHandle handle, CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var seg = AcquireSegment();
                if (seg == null) return; // nada mais a fazer

                try
                {
                    await DownloadSegmentAsync(seg, handle, stop.Token);
                }
                finally
                {
                    lock (_lock) seg.Active = false;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // pausa ou falha em outro worker
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _fatal, ex, null);
            stop.Cancel(); // derruba os demais workers
        }
    }

    /// <summary>
    /// Entrega o próximo trabalho: um segmento livre ou, se não houver,
    /// metade do segmento ativo que ainda tem mais bytes (divisão dinâmica).
    /// </summary>
    private Segment? AcquireSegment()
    {
        lock (_lock)
        {
            foreach (var s in _state.Segments)
            {
                if (!s.Active && s.Remaining > 0)
                {
                    s.Active = true;
                    return s;
                }
            }

            Segment? victim = null;
            long best = 0;
            foreach (var s in _state.Segments)
            {
                if (s.Active && s.Remaining > best)
                {
                    best = s.Remaining;
                    victim = s;
                }
            }

            if (victim == null || best < MinSplitBytes)
                return null;

            long pos = victim.Start + victim.Downloaded;
            long newStart = pos + best / 2;

            var created = new Segment { Start = newStart, End = victim.End, Downloaded = 0, Active = true };
            victim.End = newStart - 1; // o worker da vítima passa a parar no novo fim
            _state.Segments.Add(created);
            return created;
        }
    }

    private async Task DownloadSegmentAsync(Segment seg, SafeFileHandle handle, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int failures = 0;
            while (true)
            {
                long before;
                lock (_lock)
                {
                    if (seg.Remaining <= 0) return;
                    before = seg.Start + seg.Downloaded;
                }

                try
                {
                    await StreamRangeAsync(seg, handle, buffer, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (!IsFatal(ex))
                {
                    // Erro transitório (queda, timeout, 5xx): tenta de novo de onde parou.
                    long after;
                    lock (_lock) after = seg.Start + seg.Downloaded;

                    failures = after > before ? 1 : failures + 1;
                    if (failures > MaxRetries)
                        throw new FatalDownloadException($"Muitas falhas seguidas: {ex.Message}", ex);

                    double wait = Math.Min(30, Math.Pow(2, failures - 1));
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    continue;
                }

                // A conexão terminou sem erro: segmento completo, ou o servidor fechou cedo.
                long pos;
                bool done;
                lock (_lock)
                {
                    done = seg.Remaining <= 0;
                    pos = seg.Start + seg.Downloaded;
                }

                if (done) return;

                failures = pos > before ? 0 : failures + 1;
                if (failures > MaxRetries)
                    throw new FatalDownloadException("O servidor encerra a conexão sem enviar dados.");
                if (pos == before)
                    await Task.Delay(1000, ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task StreamRangeAsync(Segment seg, SafeFileHandle handle, byte[] buffer, CancellationToken ct)
    {
        long pos, end;
        lock (_lock)
        {
            pos = seg.Start + seg.Downloaded;
            end = seg.End;
        }
        if (pos > end) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(IoTimeout);

        using var req = NewRequest(_state.FinalUrl, pos, end);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (resp.StatusCode != HttpStatusCode.PartialContent)
        {
            if (resp.StatusCode == HttpStatusCode.OK)
                throw new FatalDownloadException("O servidor ignorou o cabeçalho Range.");

            resp.EnsureSuccessStatusCode(); // lança HttpRequestException com StatusCode
            throw new FatalDownloadException($"Resposta inesperada do servidor: {(int)resp.StatusCode}");
        }

        if (resp.Content.Headers.ContentRange?.From != pos)
            throw new FatalDownloadException("A faixa devolvida pelo servidor não confere com a pedida.");

        await using var net = await resp.Content.ReadAsStreamAsync(cts.Token);

        while (true)
        {
            cts.CancelAfter(IoTimeout); // reinicia o timeout a cada leitura
            int n = await net.ReadAsync(buffer.AsMemory(0, BufferSize), cts.Token);
            if (n == 0) return;

            // O segmento pode ter sido encurtado por uma divisão: só gravamos o que ainda é nosso.
            long writePos;
            int allowed;
            lock (_lock)
            {
                writePos = seg.Start + seg.Downloaded;
                allowed = (int)Math.Min(n, seg.End - writePos + 1);
            }
            if (allowed <= 0) return;

            try
            {
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, allowed), writePos, ct);
            }
            catch (IOException ex)
            {
                throw new FatalDownloadException($"Erro ao gravar no disco: {ex.Message}", ex);
            }

            bool finished;
            lock (_lock)
            {
                seg.Downloaded += allowed; // só conta depois de gravar de fato
                finished = seg.Remaining <= 0;
            }

            if (finished || allowed < n) return;
        }
    }

    private static bool IsFatal(Exception ex)
    {
        if (ex is FatalDownloadException or UnauthorizedAccessException)
            return true;

        if (ex is HttpRequestException { StatusCode: { } code })
        {
            int c = (int)code;
            return c is >= 400 and < 500 && c != 408 && c != 429;
        }

        return false;
    }

    private long SumDownloaded()
    {
        lock (_lock) return _state.Segments.Sum(s => s.Downloaded);
    }

    private int ActiveCount()
    {
        lock (_lock) return _state.Segments.Count(s => s.Active && s.Remaining > 0);
    }

    // ------------------------------------------------------------------
    // Conexão única (servidor sem Range)
    // ------------------------------------------------------------------

    private async Task<DownloadOutcome> DownloadSingleAsync(ProbeResult p, string outputDir, CancellationToken ct)
    {
        string partPath = Path.Combine(outputDir, p.FileName + ".part");
        long done = 0;

        var work = Task.Run(async () =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(IoTimeout);

            using var req = NewRequest(p.FinalUrl, null, null);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            resp.EnsureSuccessStatusCode();

            await using var net = await resp.Content.ReadAsStreamAsync(cts.Token);
            await using var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1, useAsync: true);

            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    cts.CancelAfter(IoTimeout);
                    int n = await net.ReadAsync(buffer.AsMemory(0, BufferSize), cts.Token);
                    if (n == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    Interlocked.Add(ref done, n);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });

        try
        {
            await ReportLoopAsync(work, () => Interlocked.Read(ref done), p.TotalSize, () => 1, null);
            await work; // propaga erro, se houver
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(partPath);
            Log?.Invoke("Pausado. Sem suporte a Range não dá para retomar; o arquivo parcial foi descartado.");
            return DownloadOutcome.Paused;
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            Log?.Invoke($"Falha: {ex.Message}");
            return DownloadOutcome.Failed;
        }

        FinalPath = Finish(partPath, outputDir, p.FileName, null);
        return DownloadOutcome.Completed;
    }

    // ------------------------------------------------------------------
    // Utilidades
    // ------------------------------------------------------------------

    private async Task ReportLoopAsync(
        Task completion, Func<long> done, long? total, Func<int> connections, Action? periodicSave)
    {
        var clock = Stopwatch.StartNew();
        long lastDone = done();
        var lastTick = clock.Elapsed;
        var lastSave = clock.Elapsed;
        double speed = 0;

        while (!completion.IsCompleted)
        {
            await Task.WhenAny(completion, Task.Delay(500));

            var now = clock.Elapsed;
            long d = done();
            double dt = (now - lastTick).TotalSeconds;
            if (dt > 0)
            {
                double instant = (d - lastDone) / dt;
                speed = speed == 0 ? instant : speed * 0.7 + instant * 0.3; // média suavizada
            }
            lastDone = d;
            lastTick = now;

            TimeSpan? eta = total is long t && speed > 1
                ? TimeSpan.FromSeconds(Math.Max(0, t - d) / speed)
                : null;

            Progress?.Invoke(new ProgressInfo(d, total, speed, connections(), eta));

            if (periodicSave != null && now - lastSave >= SaveInterval)
            {
                periodicSave();
                lastSave = now;
            }
        }

        Progress?.Invoke(new ProgressInfo(done(), total, 0, 0, null));
    }

    private static HttpRequestMessage NewRequest(string url, long? from, long? to)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url)
        {
            // HTTP/1.1 força uma conexão TCP por segmento. Em HTTP/2 tudo iria
            // multiplexado numa conexão só e o ganho de velocidade sumiria.
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        if (from != null || to != null)
            req.Headers.Range = new RangeHeaderValue(from, to);

        return req;
    }

    private string Finish(string partPath, string outputDir, string fileName, string? statePath)
    {
        string finalPath = UniquePath(Path.Combine(outputDir, fileName));
        File.Move(partPath, finalPath);
        if (statePath != null) TryDelete(statePath);
        return finalPath;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignora */ }
    }
}

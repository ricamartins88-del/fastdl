# FastDl — gerenciador de downloads para Windows (WPF / .NET 8)

## Como rodar
1. Instale o SDK: `winget install Microsoft.DotNet.SDK.8`
2. Na pasta do projeto: `dotnet run`

## Gerar o .exe único (para instalar de vez)
    dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
O executável fica em `bin\Release\net8.0-windows\win-x64\publish\FastDl.exe`.

## Como usar
- Cole o link e aperte Enter (ou "Baixar"). Também dá para arrastar um link para a janela.
- Ao voltar para a janela, se a área de transferência tem um link, o campo já vem preenchido.
- Pausar / Retomar em cada item, ou "Pausar todos" / "Retomar todos".
- Fechar o app pausa tudo com segurança; ao abrir de novo, retome com o botão ▶.
- Clique direito num item: copiar link, abrir pasta, remover (com ou sem apagar arquivos).

## Onde ficam os dados
- Lista e configurações: `%AppData%\FastDl\` (downloads.json, settings.json, crash.log)
- Progresso de cada download: ao lado do arquivo, em `nome.part` + `nome.fdl`

## Testar sem internet
    python test_server.py        (servidor local, 1 MB/s por conexão)
    link para colar no app: http://localhost:8000/teste.bin
Compare conexões = 1 com conexões = 8 para ver a aceleração. Para conferir a integridade:
    certutil -hashfile "<pasta>\teste.bin" SHA256   (deve bater com o hash impresso pelo servidor)

## Arquitetura
- `DownloadEngine.cs` — motor (segmentos, divisão dinâmica, retomada, retentativas). Sem UI.
- `DownloadItemViewModel.cs` — um download na lista; controla o próprio motor.
- `MainViewModel.cs` — lista, fila (downloads simultâneos), configurações, persistência.
- `MainWindow.xaml` — tela.

## Compilar na nuvem (sem instalar nada)
O arquivo `.github/workflows/build.yml` faz o GitHub gerar o FastDl.exe:
1. Suba o conteúdo desta pasta num repositório do GitHub.
2. Aba **Actions** > **Build FastDl** > aguarde (2 a 4 min).
3. Abra a execução e baixe o arquivo em **Artifacts** (FastDl-win-x64).

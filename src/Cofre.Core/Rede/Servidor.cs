using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Cofre.Core.Armazenamento;
using Cofre.Core.Comandos;
using Cofre.Core.Protocolo;

namespace Cofre.Core.Rede;

/// <summary>
/// Servidor TCP: uma tarefa por conexão lê RESP com System.IO.Pipelines, e todos os comandos passam por um único
/// laço de execução (canal), o que dá atomicidade sem travas, como o modelo do Redis. Um ciclo periódico faz a
/// expiração ativa por amostragem.
/// </summary>
public sealed class Servidor : IAsyncDisposable
{
    private readonly Executor _executor;
    private readonly TcpListener _listener;
    private readonly Channel<(RespValor Pedido, TaskCompletionSource<RespValor> Resposta)> _fila = Channel.CreateUnbounded<(RespValor, TaskCompletionSource<RespValor>)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _laco;
    private Task? _aceitar;
    private Task? _expiracao;
    private int _conexoes;

    public Servidor(Executor executor, IPAddress? endereco = null, int porta = 6379)
    {
        _executor = executor;
        _listener = new TcpListener(endereco ?? IPAddress.Loopback, porta);
    }

    public int Porta => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Conexoes => _conexoes;

    public void Iniciar()
    {
        _listener.Start();
        _laco = LacoDeExecucao(_cts.Token);
        _aceitar = Aceitar(_cts.Token);
        _expiracao = ExpiracaoAtiva(_cts.Token);
    }

    /// <summary>Executa um comando pela mesma fila das conexões (útil para testes e para a CLI embutida).</summary>
    public async Task<RespValor> ExecutarAsync(RespValor pedido)
    {
        var tcs = new TaskCompletionSource<RespValor>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _fila.Writer.WriteAsync((pedido, tcs));
        return await tcs.Task;
    }

    private async Task LacoDeExecucao(CancellationToken ct)
    {
        try
        {
            await foreach (var (pedido, tcs) in _fila.Reader.ReadAllAsync(ct))
            {
                try { tcs.SetResult(pedido is ExpirarPedido ? new RespValor.Inteiro(_executor.Banco.ExpirarAmostra()) : _executor.Executar(pedido)); }
                catch (Exception e) { tcs.SetResult(new RespValor.Erro($"ERR {e.Message}")); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Aceitar(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var cliente = await _listener.AcceptTcpClientAsync(ct);
                _ = Atender(cliente, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task Atender(TcpClient cliente, CancellationToken ct)
    {
        Interlocked.Increment(ref _conexoes);
        try
        {
            using var _ = cliente;
            cliente.NoDelay = true;
            var stream = cliente.GetStream();
            var leitor = PipeReader.Create(stream);
            var escritor = PipeWriter.Create(stream);
            while (!ct.IsCancellationRequested)
            {
                var resultado = await leitor.ReadAsync(ct);
                var buffer = resultado.Buffer;
                var fechar = false;
                try
                {
                    while (Resp.TentarLer(ref buffer, out var pedido))
                    {
                        var resposta = await ExecutarAsync(pedido!);
                        Resp.Escrever(escritor, resposta);
                        if (pedido is RespValor.Lista { Itens: [RespValor.Bulk { Texto: var cmd }, ..] } && string.Equals(cmd, "QUIT", StringComparison.OrdinalIgnoreCase)) fechar = true;
                    }
                }
                catch (RespException e)
                {
                    Resp.Escrever(escritor, new RespValor.Erro($"ERR Protocol error: {e.Message}"));
                    fechar = true;
                }
                await escritor.FlushAsync(ct);
                leitor.AdvanceTo(buffer.Start, buffer.End);
                if (fechar || resultado.IsCompleted) break;
            }
            await leitor.CompleteAsync();
            await escritor.CompleteAsync();
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally { Interlocked.Decrement(ref _conexoes); }
    }

    private async Task ExpiracaoAtiva(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var tcs = new TaskCompletionSource<RespValor>(TaskCreationOptions.RunContinuationsAsynchronously);
                // executa no laço para não competir com os comandos
                await _fila.Writer.WriteAsync((new ExpirarPedido(), tcs), ct);
                await tcs.Task;
            }
        }
        catch (OperationCanceledException) { }
    }

    private sealed record ExpirarPedido : RespValor;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _fila.Writer.TryComplete();
        foreach (var t in new[] { _laco, _aceitar, _expiracao }) if (t is not null) { try { await t; } catch (OperationCanceledException) { } }
        _cts.Dispose();
    }
}

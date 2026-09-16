using System.Buffers;
using System.Net.Sockets;
using System.Text;
using Cofre.Core.Armazenamento;
using Cofre.Core.Comandos;
using Cofre.Core.Protocolo;
using Cofre.Core.Rede;

namespace Cofre.Tests;

public class ServidorTests
{
    private static async Task<(Servidor, TcpClient, NetworkStream)> Subir()
    {
        var servidor = new Servidor(new Executor(new Banco()), porta: 0);
        servidor.Iniciar();
        var cliente = new TcpClient();
        await cliente.ConnectAsync("127.0.0.1", servidor.Porta);
        return (servidor, cliente, cliente.GetStream());
    }

    private static async Task<RespValor> Roundtrip(NetworkStream s, RespValor pedido)
    {
        await s.WriteAsync(Resp.Serializar(pedido));
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (true)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total));
            n.Should().BeGreaterThan(0, "conexão fechada");
            total += n;
            var seq = new ReadOnlySequence<byte>(buffer, 0, total);
            if (Resp.TentarLer(ref seq, out var v)) return v!;
        }
    }

    [Fact]
    public async Task ConversaPorTcpEmResp()
    {
        var (servidor, cliente, s) = await Subir();
        await using var _ = servidor;
        using var __ = cliente;
        (await Roundtrip(s, Resp.Comando("PING"))).Should().Be(RespValor.Pong);
        (await Roundtrip(s, Resp.Comando("SET", "k", "v"))).Should().Be(RespValor.Ok);
        ((RespValor.Bulk)await Roundtrip(s, Resp.Comando("GET", "k"))).Texto.Should().Be("v");
        ((RespValor.Inteiro)await Roundtrip(s, Resp.Comando("INCR", "n"))).Valor.Should().Be(1);
    }

    [Fact]
    public async Task AceitaComandoInlineComoTelnet()
    {
        var (servidor, cliente, s) = await Subir();
        await using var _ = servidor;
        using var __ = cliente;
        await s.WriteAsync(Encoding.UTF8.GetBytes("SET a 1\r\nGET a\r\n"));
        var buffer = new byte[256];
        var total = 0;
        while (!Encoding.UTF8.GetString(buffer, 0, total).Contains("$1\r\n1\r\n")) total += await s.ReadAsync(buffer.AsMemory(total));
        Encoding.UTF8.GetString(buffer, 0, total).Should().Be("+OK\r\n$1\r\n1\r\n");
    }

    [Fact]
    public async Task PipelineDeComandosEUmaEscrita()
    {
        var (servidor, cliente, s) = await Subir();
        await using var _ = servidor;
        using var __ = cliente;
        var lote = Enumerable.Range(0, 100).SelectMany(_ => Resp.Serializar(Resp.Comando("INCR", "c"))).ToArray();
        await s.WriteAsync(lote);
        var esperado = string.Concat(Enumerable.Range(1, 100).Select(k => $":{k}\r\n"));
        var buffer = new byte[esperado.Length];
        var total = 0;
        while (total < buffer.Length) total += await s.ReadAsync(buffer.AsMemory(total));
        Encoding.UTF8.GetString(buffer).Should().Be(esperado);
    }

    [Fact]
    public async Task ClientesConcorrentesNaoPerdemIncrementos()
    {
        var servidor = new Servidor(new Executor(new Banco()), porta: 0);
        servidor.Iniciar();
        await using var _ = servidor;
        var tarefas = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var c = new TcpClient();
            await c.ConnectAsync("127.0.0.1", servidor.Porta);
            var s = c.GetStream();
            for (var k = 0; k < 50; k++) await Roundtrip(s, Resp.Comando("INCR", "total"));
        });
        await Task.WhenAll(tarefas);
        var v = await servidor.ExecutarAsync(Resp.Comando("GET", "total"));
        ((RespValor.Bulk)v).Texto.Should().Be("400");
    }

    [Fact]
    public async Task ErroDeProtocoloFechaAConexao()
    {
        var (servidor, cliente, s) = await Subir();
        await using var _ = servidor;
        using var __ = cliente;
        await s.WriteAsync(Encoding.UTF8.GetBytes("$abc\r\n"));
        var buffer = new byte[256];
        var n = await s.ReadAsync(buffer);
        Encoding.UTF8.GetString(buffer, 0, n).Should().StartWith("-ERR Protocol error");
        (await s.ReadAsync(buffer)).Should().Be(0, "servidor fecha a conexão");
    }
}

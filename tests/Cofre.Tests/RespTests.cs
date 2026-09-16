using System.Buffers;
using System.Text;
using Cofre.Core.Protocolo;

namespace Cofre.Tests;

public class RespTests
{
    private static string S(RespValor v) => Encoding.UTF8.GetString(Resp.Serializar(v));

    [Fact]
    public void SerializaOsCincoTipos()
    {
        S(RespValor.Ok).Should().Be("+OK\r\n");
        S(new RespValor.Erro("ERR x")).Should().Be("-ERR x\r\n");
        S(new RespValor.Inteiro(-42)).Should().Be(":-42\r\n");
        S(RespValor.Bulk.De("olá")).Should().Be("$4\r\nolá\r\n"); // 4 bytes em UTF-8
        S(RespValor.Nulo).Should().Be("$-1\r\n");
        S(new RespValor.Lista(null)).Should().Be("*-1\r\n");
        S(Resp.Comando("SET", "a", "1")).Should().Be("*3\r\n$3\r\nSET\r\n$1\r\na\r\n$1\r\n1\r\n");
    }

    [Fact]
    public void LeOQueSerializou()
    {
        var original = new RespValor.Lista(new RespValor[] { RespValor.Ok, new RespValor.Inteiro(7), RespValor.Bulk.De("x\r\ny"), RespValor.Nulo, new RespValor.Erro("e"), new RespValor.Lista(null) });
        var buffer = new ReadOnlySequence<byte>(Resp.Serializar(original));
        Resp.TentarLer(ref buffer, out var lido).Should().BeTrue();
        lido.Should().BeOfType<RespValor.Lista>().Which.Itens.Should().HaveCount(6);
        ((RespValor.Bulk)((RespValor.Lista)lido!).Itens![2]).Texto.Should().Be("x\r\ny");
        buffer.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void LeituraIncrementalDevolveFalsoAteCompletar()
    {
        var completo = Encoding.UTF8.GetBytes("*2\r\n$3\r\nGET\r\n$5\r\nchave\r\n");
        for (var corte = 1; corte < completo.Length; corte++)
        {
            var parcial = new ReadOnlySequence<byte>(completo[..corte]);
            Resp.TentarLer(ref parcial, out _).Should().BeFalse($"corte em {corte}");
            parcial.Length.Should().Be(corte, "não deve consumir nada quando incompleto");
        }
        var tudo = new ReadOnlySequence<byte>(completo);
        Resp.TentarLer(ref tudo, out var v).Should().BeTrue();
        ((RespValor.Bulk)((RespValor.Lista)v!).Itens![1]).Texto.Should().Be("chave");
    }

    [Fact]
    public void LeDoisComandosNoMesmoBufferEDeixaOResto()
    {
        var bytes = Encoding.UTF8.GetBytes("+A\r\n:1\r\n$2\r\nab");
        var buffer = new ReadOnlySequence<byte>(bytes);
        Resp.TentarLer(ref buffer, out var a).Should().BeTrue();
        Resp.TentarLer(ref buffer, out var b).Should().BeTrue();
        Resp.TentarLer(ref buffer, out _).Should().BeFalse();
        a.Should().Be(new RespValor.Simples("A"));
        b.Should().Be(new RespValor.Inteiro(1));
        buffer.Length.Should().Be(6);
    }

    [Fact]
    public void ProtocoloInlineViraArrayDeBulk()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("SET  a   b\r\n"));
        Resp.TentarLer(ref buffer, out var v).Should().BeTrue();
        var itens = ((RespValor.Lista)v!).Itens!;
        itens.Select(i => ((RespValor.Bulk)i).Texto).Should().Equal("SET", "a", "b");
    }

    [Fact]
    public void RejeitaTamanhosInvalidos()
    {
        var buffer = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("$abc\r\n"));
        var act = () => Resp.TentarLer(ref buffer, out _);
        act.Should().Throw<RespException>();
    }
}

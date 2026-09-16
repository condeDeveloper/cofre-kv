using Cofre.Core.Armazenamento;
using Cofre.Core.Comandos;
using Cofre.Core.Protocolo;

namespace Cofre.Tests;

public class ExecutorTests
{
    private readonly RelogioManual _relogio = new(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly Executor _ex;

    public ExecutorTests() => _ex = new Executor(new Banco(_relogio));

    private RespValor R(params string[] args) => _ex.Executar(args);
    private static string? Texto(RespValor v) => ((RespValor.Bulk)v).Texto;
    private static long Int(RespValor v) => ((RespValor.Inteiro)v).Valor;
    private static IEnumerable<string?> Lista(RespValor v) => ((RespValor.Lista)v).Itens!.Select(i => ((RespValor.Bulk)i).Texto);

    [Fact]
    public void StringsBasicas()
    {
        R("PING").Should().Be(RespValor.Pong);
        Texto(R("PING", "oi")).Should().Be("oi");
        R("SET", "a", "1").Should().Be(RespValor.Ok);
        Texto(R("GET", "a")).Should().Be("1");
        R("GET", "nada").Should().Be(RespValor.Nulo);
        Int(R("APPEND", "a", "23")).Should().Be(3);
        Int(R("STRLEN", "a")).Should().Be(3);
        R("MSET", "b", "2", "c", "3").Should().Be(RespValor.Ok);
        Lista(R("MGET", "a", "b", "x")).Should().Equal("123", "2", null);
        Int(R("DEL", "a", "b", "x")).Should().Be(2);
        Int(R("EXISTS", "c", "a")).Should().Be(1);
        Int(R("SETNX", "c", "9")).Should().Be(0);
        Int(R("SETNX", "d", "9")).Should().Be(1);
        Texto(R("GETDEL", "d")).Should().Be("9");
        Int(R("EXISTS", "d")).Should().Be(0);
    }

    [Fact]
    public void ContadoresEErros()
    {
        Int(R("INCR", "n")).Should().Be(1);
        Int(R("INCRBY", "n", "10")).Should().Be(11);
        Int(R("DECR", "n")).Should().Be(10);
        Int(R("DECRBY", "n", "4")).Should().Be(6);
        R("SET", "s", "abc");
        R("INCR", "s").Should().BeOfType<RespValor.Erro>().Which.Mensagem.Should().Contain("not an integer");
        R("SET", "big", long.MaxValue.ToString());
        R("INCR", "big").Should().BeOfType<RespValor.Erro>().Which.Mensagem.Should().Contain("overflow");
        R("GET").Should().BeOfType<RespValor.Erro>().Which.Mensagem.Should().Contain("wrong number of arguments");
        R("XPTO").Should().BeOfType<RespValor.Erro>().Which.Mensagem.Should().Contain("unknown command");
    }

    [Fact]
    public void SetComOpcoesNxXxEx()
    {
        R("SET", "k", "1", "NX").Should().Be(RespValor.Ok);
        R("SET", "k", "2", "NX").Should().Be(RespValor.Nulo);
        R("SET", "outra", "2", "XX").Should().Be(RespValor.Nulo);
        R("SET", "k", "3", "XX", "EX", "10").Should().Be(RespValor.Ok);
        Texto(R("GET", "k")).Should().Be("3");
        Int(R("TTL", "k")).Should().Be(10);
        R("SET", "k", "1", "FOO").Should().BeOfType<RespValor.Erro>();
    }

    [Fact]
    public void ExpiracaoComRelogioManual()
    {
        R("SET", "t", "x", "PX", "1500");
        Int(R("PTTL", "t")).Should().Be(1500);
        Int(R("TTL", "t")).Should().Be(2); // arredonda para cima
        _relogio.Avancar(TimeSpan.FromMilliseconds(1499));
        Texto(R("GET", "t")).Should().Be("x");
        _relogio.Avancar(TimeSpan.FromMilliseconds(1));
        R("GET", "t").Should().Be(RespValor.Nulo);
        Int(R("TTL", "t")).Should().Be(-2);
        Int(R("DBSIZE")).Should().Be(0);

        R("SET", "p", "1");
        Int(R("TTL", "p")).Should().Be(-1);
        Int(R("EXPIRE", "p", "5")).Should().Be(1);
        Int(R("PERSIST", "p")).Should().Be(1);
        Int(R("TTL", "p")).Should().Be(-1);
        Int(R("EXPIRE", "inexistente", "5")).Should().Be(0);
    }

    [Fact]
    public void ExpiracaoAtivaRemoveVencidas()
    {
        for (var k = 0; k < 50; k++) R("SET", $"k{k}", "v", "EX", "1");
        R("SET", "fixa", "v");
        _relogio.Avancar(TimeSpan.FromSeconds(2));
        _ex.Banco.Tamanho.Should().Be(51, "expiração passiva só ocorre no acesso");
        _ex.Banco.ExpirarAmostra(20).Should().Be(20);
        _ex.Banco.ExpirarAmostra(100).Should().Be(30);
        _ex.Banco.Tamanho.Should().Be(1);
        _ex.Banco.Expiracoes.Should().Be(50);
    }

    [Fact]
    public void ListasComoDeque()
    {
        Int(R("RPUSH", "l", "b", "c")).Should().Be(2);
        Int(R("LPUSH", "l", "a")).Should().Be(3);
        Lista(R("LRANGE", "l", "0", "-1")).Should().Equal("a", "b", "c");
        Lista(R("LRANGE", "l", "-2", "-1")).Should().Equal("b", "c");
        Lista(R("LRANGE", "l", "5", "10")).Should().BeEmpty();
        Texto(R("LINDEX", "l", "-1")).Should().Be("c");
        R("LINDEX", "l", "9").Should().Be(RespValor.Nulo);
        Texto(R("LPOP", "l")).Should().Be("a");
        Lista(R("RPOP", "l", "5")).Should().Equal("c", "b");
        Int(R("LLEN", "l")).Should().Be(0);
        Int(R("EXISTS", "l")).Should().Be(0, "lista vazia é removida");
        R("LPOP", "l").Should().Be(RespValor.Nulo);
        R("SET", "s", "1");
        R("LPUSH", "s", "x").Should().BeOfType<RespValor.Erro>().Which.Mensagem.Should().StartWith("WRONGTYPE");
        ((RespValor.Simples)R("TYPE", "s")).Texto.Should().Be("string");
        ((RespValor.Simples)R("TYPE", "nada")).Texto.Should().Be("none");
    }

    [Fact]
    public void Hashes()
    {
        Int(R("HSET", "h", "nome", "Ana", "idade", "30")).Should().Be(2);
        Int(R("HSET", "h", "idade", "31")).Should().Be(0);
        Texto(R("HGET", "h", "idade")).Should().Be("31");
        R("HGET", "h", "x").Should().Be(RespValor.Nulo);
        Int(R("HLEN", "h")).Should().Be(2);
        Int(R("HEXISTS", "h", "nome")).Should().Be(1);
        Int(R("HINCRBY", "h", "idade", "1")).Should().Be(32);
        Int(R("HINCRBY", "h", "visitas", "5")).Should().Be(5);
        Lista(R("HKEYS", "h")).Should().BeEquivalentTo("nome", "idade", "visitas");
        Lista(R("HGETALL", "h")).Should().HaveCount(6).And.Contain("Ana");
        Int(R("HDEL", "h", "nome", "idade", "visitas", "zzz")).Should().Be(3);
        Int(R("EXISTS", "h")).Should().Be(0);
        ((RespValor.Simples)R("TYPE", "h")).Texto.Should().Be("none");
        R("HSET", "h", "so-campo").Should().BeOfType<RespValor.Erro>();
    }

    [Fact]
    public void KeysComGlobERename()
    {
        R("MSET", "user:1", "a", "user:2", "b", "usuario", "c", "u1", "d");
        Lista(R("KEYS", "user:*")).Should().Equal("user:1", "user:2");
        Lista(R("KEYS", "u?")).Should().Equal("u1");
        Lista(R("KEYS", "user:[12]")).Should().HaveCount(2);
        Lista(R("KEYS", "user:[^1]")).Should().Equal("user:2");
        Lista(R("KEYS", "*")).Should().HaveCount(4);
        R("RENAME", "u1", "u2").Should().Be(RespValor.Ok);
        Texto(R("GET", "u2")).Should().Be("d");
        R("RENAME", "nada", "x").Should().BeOfType<RespValor.Erro>();
        R("FLUSHDB").Should().Be(RespValor.Ok);
        Int(R("DBSIZE")).Should().Be(0);
    }

    [Fact]
    public void LruDespejaAMenosUsada()
    {
        var ex = new Executor(new Banco(_relogio, maximoDeChaves: 3));
        ex.Executar("SET", "a", "1");
        ex.Executar("SET", "b", "2");
        ex.Executar("SET", "c", "3");
        ex.Executar("GET", "a"); // a vira a mais recente; b é a menos usada
        ex.Executar("SET", "d", "4");
        ex.Banco.Tamanho.Should().Be(3);
        ex.Executar("GET", "b").Should().Be(RespValor.Nulo);
        ex.Executar("GET", "a").Should().NotBe(RespValor.Nulo);
        ex.Banco.Despejos.Should().Be(1);
        ex.Executar("SET", "a", "novo"); // sobrescrever não despeja
        ex.Banco.Despejos.Should().Be(1);
        ((RespValor.Bulk)ex.Executar("INFO")).Texto.Should().Contain("evicted_keys:1").And.Contain("db0:keys=3,maxkeys=3");
    }

    [Fact]
    public void ExecutaAPartirDeRespEValidaProtocolo()
    {
        _ex.Executar(Resp.Comando("set", "x", "1")).Should().Be(RespValor.Ok);
        _ex.Executar(new RespValor.Inteiro(1)).Should().BeOfType<RespValor.Erro>();
        _ex.Executar(new RespValor.Lista(new RespValor[] { new RespValor.Inteiro(1) })).Should().BeOfType<RespValor.Erro>();
    }
}

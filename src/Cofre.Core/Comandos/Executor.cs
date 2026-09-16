using System.Globalization;
using System.Text;
using Cofre.Core.Armazenamento;
using Cofre.Core.Protocolo;

namespace Cofre.Core.Comandos;

/// <summary>
/// Interpreta comandos no dialeto do Redis sobre um <see cref="Banco"/>. Strings, contadores, expiração,
/// listas (deque) e hashes, mais comandos de servidor. Uma chamada por comando; o servidor garante a serialização.
/// </summary>
public sealed class Executor(Banco banco)
{
    public Banco Banco { get; } = banco;
    public long ComandosProcessados { get; private set; }
    public DateTimeOffset InicioEm { get; } = banco.Agora;

    public RespValor Executar(RespValor pedido)
    {
        if (pedido is not RespValor.Lista { Itens: { Count: > 0 } itens }) return new RespValor.Erro("ERR protocolo: esperado array de bulk strings");
        var args = new string[itens.Count];
        for (var k = 0; k < itens.Count; k++)
        {
            if (itens[k] is not RespValor.Bulk { Bytes: not null } b) return new RespValor.Erro("ERR protocolo: argumentos devem ser bulk strings");
            args[k] = Encoding.UTF8.GetString(b.Bytes);
        }
        return Executar(args);
    }

    public RespValor Executar(params string[] args)
    {
        ComandosProcessados++;
        var nome = args[0].ToUpperInvariant();
        try
        {
            return nome switch
            {
                "PING" => args.Length > 1 ? RespValor.Bulk.De(args[1]) : (RespValor)RespValor.Pong,
                "ECHO" => Aridade(args, 2) ?? RespValor.Bulk.De(args[1]),
                "SET" => Set(args),
                "GET" => Aridade(args, 2) ?? Get(args[1]),
                "GETDEL" => Aridade(args, 2) ?? GetDel(args[1]),
                "MGET" => new RespValor.Lista(args.Skip(1).Select(Get).ToList()),
                "MSET" => MSet(args),
                "SETNX" => Aridade(args, 3) ?? SetNx(args[1], args[2]),
                "APPEND" => Aridade(args, 3) ?? Append(args[1], args[2]),
                "STRLEN" => Aridade(args, 2) ?? new RespValor.Inteiro(Banco.TentarObter(args[1], out var e) ? Encoding.UTF8.GetByteCount(Str(e)) : 0),
                "INCR" => Aridade(args, 2) ?? IncrBy(args[1], 1),
                "DECR" => Aridade(args, 2) ?? IncrBy(args[1], -1),
                "INCRBY" => Aridade(args, 3) ?? IncrBy(args[1], Long(args[2])),
                "DECRBY" => Aridade(args, 3) ?? IncrBy(args[1], -Long(args[2])),
                "DEL" => new RespValor.Inteiro(args.Skip(1).Count(Banco.Remover)),
                "EXISTS" => new RespValor.Inteiro(args.Skip(1).Count(Banco.Existe)),
                "EXPIRE" => Aridade(args, 3) ?? Expire(args[1], TimeSpan.FromSeconds(Long(args[2]))),
                "PEXPIRE" => Aridade(args, 3) ?? Expire(args[1], TimeSpan.FromMilliseconds(Long(args[2]))),
                "PERSIST" => Aridade(args, 2) ?? new RespValor.Inteiro(Banco.TtlMs(args[1]) >= 0 && Banco.DefinirExpiracao(args[1], null) ? 1 : 0),
                "TTL" => Aridade(args, 2) ?? new RespValor.Inteiro(Ttl(args[1], 1000)),
                "PTTL" => Aridade(args, 2) ?? new RespValor.Inteiro(Ttl(args[1], 1)),
                "TYPE" => Aridade(args, 2) ?? new RespValor.Simples(Banco.TentarObter(args[1], out var e) ? e.Tipo.ToString().ToLowerInvariant() : "none"),
                "KEYS" => Aridade(args, 2) ?? new RespValor.Lista(Banco.Chaves(args[1]).OrderBy(k => k, StringComparer.Ordinal).Select(k => (RespValor)RespValor.Bulk.De(k)).ToList()),
                "RENAME" => Aridade(args, 3) ?? Rename(args[1], args[2]),
                "LPUSH" => Push(args, esquerda: true),
                "RPUSH" => Push(args, esquerda: false),
                "LPOP" => Pop(args, esquerda: true),
                "RPOP" => Pop(args, esquerda: false),
                "LLEN" => Aridade(args, 2) ?? new RespValor.Inteiro(Lista(args[1])?.Count ?? 0),
                "LRANGE" => Aridade(args, 4) ?? LRange(args[1], Long(args[2]), Long(args[3])),
                "LINDEX" => Aridade(args, 3) ?? LIndex(args[1], Long(args[2])),
                "HSET" => HSet(args),
                "HGET" => Aridade(args, 3) ?? (Hash(args[1]) is { } h && h.TryGetValue(args[2], out var v) ? RespValor.Bulk.De(v) : RespValor.Nulo),
                "HDEL" => HDel(args),
                "HEXISTS" => Aridade(args, 3) ?? new RespValor.Inteiro(Hash(args[1])?.ContainsKey(args[2]) == true ? 1 : 0),
                "HLEN" => Aridade(args, 2) ?? new RespValor.Inteiro(Hash(args[1])?.Count ?? 0),
                "HKEYS" => Aridade(args, 2) ?? Strings(Hash(args[1])?.Keys ?? Enumerable.Empty<string>()),
                "HVALS" => Aridade(args, 2) ?? Strings(Hash(args[1])?.Values ?? Enumerable.Empty<string>()),
                "HGETALL" => Aridade(args, 2) ?? Strings((Hash(args[1]) ?? new()).SelectMany(kv => new[] { kv.Key, kv.Value })),
                "HINCRBY" => Aridade(args, 4) ?? HIncrBy(args[1], args[2], Long(args[3])),
                "DBSIZE" => new RespValor.Inteiro(Banco.Tamanho),
                "FLUSHDB" or "FLUSHALL" => Flush(),
                "INFO" => RespValor.Bulk.De(Info()),
                "COMMAND" => RespValor.ListaVazia,
                "QUIT" => RespValor.Ok,
                _ => new RespValor.Erro($"ERR unknown command '{args[0]}'"),
            };
        }
        catch (TipoErradoException e) { return new RespValor.Erro(e.Message); }
        catch (FormatException e) { return new RespValor.Erro($"ERR {e.Message}"); }
        catch (OverflowException) { return new RespValor.Erro("ERR increment or decrement would overflow"); }
        catch (ArgumentException e) { return new RespValor.Erro($"ERR {e.Message}"); }
    }

    // ---- strings ----

    private RespValor Set(string[] a)
    {
        if (a.Length < 3) return ErroAridade("set");
        DateTimeOffset? expira = null;
        bool nx = false, xx = false;
        for (var k = 3; k < a.Length; k++)
        {
            switch (a[k].ToUpperInvariant())
            {
                case "EX": expira = Banco.Agora.AddSeconds(Positivo(Long(Arg(a, ++k)))); break;
                case "PX": expira = Banco.Agora.AddMilliseconds(Positivo(Long(Arg(a, ++k)))); break;
                case "NX": nx = true; break;
                case "XX": xx = true; break;
                default: return new RespValor.Erro("ERR syntax error");
            }
        }
        var existe = Banco.Existe(a[1]);
        if ((nx && existe) || (xx && !existe)) return RespValor.Nulo;
        Banco.Definir(a[1], a[2], Tipo.String, expira);
        return RespValor.Ok;
    }

    private RespValor Get(string chave) => Banco.TentarObter(chave, out var e) ? RespValor.Bulk.De(Str(e)) : RespValor.Nulo;

    private RespValor GetDel(string chave)
    {
        var v = Get(chave);
        Banco.Remover(chave);
        return v;
    }

    private RespValor MSet(string[] a)
    {
        if (a.Length < 3 || a.Length % 2 == 0) return ErroAridade("mset");
        for (var k = 1; k < a.Length; k += 2) Banco.Definir(a[k], a[k + 1], Tipo.String);
        return RespValor.Ok;
    }

    private RespValor SetNx(string chave, string valor)
    {
        if (Banco.Existe(chave)) return new RespValor.Inteiro(0);
        Banco.Definir(chave, valor, Tipo.String);
        return new RespValor.Inteiro(1);
    }

    private RespValor Append(string chave, string sufixo)
    {
        var e = Banco.ObterOuCriar(chave, Tipo.String, () => "");
        var novo = (string)e.Valor + sufixo;
        e.Valor = novo;
        return new RespValor.Inteiro(Encoding.UTF8.GetByteCount(novo));
    }

    private RespValor IncrBy(string chave, long delta)
    {
        var e = Banco.ObterOuCriar(chave, Tipo.String, () => "0");
        var atual = long.TryParse((string)e.Valor, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException("value is not an integer or out of range");
        var novo = checked(atual + delta);
        e.Valor = novo.ToString(CultureInfo.InvariantCulture);
        return new RespValor.Inteiro(novo);
    }

    private static string Str(Entrada e) => e.Tipo == Tipo.String ? (string)e.Valor : throw new TipoErradoException();

    // ---- expiração e chaves ----

    private RespValor Expire(string chave, TimeSpan ttl) => new RespValor.Inteiro(Banco.DefinirExpiracao(chave, Banco.Agora + ttl) ? 1 : 0);

    private long Ttl(string chave, int divisor)
    {
        var ms = Banco.TtlMs(chave);
        return ms < 0 ? ms : (long)Math.Ceiling(ms / (double)divisor);
    }

    private RespValor Rename(string de, string para)
    {
        if (!Banco.TentarObter(de, out var e)) return new RespValor.Erro("ERR no such key");
        Banco.Definir(para, e.Valor, e.Tipo, e.ExpiraEm);
        Banco.Remover(de);
        return RespValor.Ok;
    }

    private RespValor Flush()
    {
        Banco.Limpar();
        return RespValor.Ok;
    }

    // ---- listas ----

    private LinkedList<string>? Lista(string chave) => Banco.TentarObter(chave, out var e) ? e.Tipo == Tipo.Lista ? (LinkedList<string>)e.Valor : throw new TipoErradoException() : null;

    private RespValor Push(string[] a, bool esquerda)
    {
        if (a.Length < 3) return ErroAridade(a[0]);
        var lista = (LinkedList<string>)Banco.ObterOuCriar(a[1], Tipo.Lista, () => new LinkedList<string>()).Valor;
        foreach (var v in a.Skip(2)) { if (esquerda) lista.AddFirst(v); else lista.AddLast(v); }
        return new RespValor.Inteiro(lista.Count);
    }

    private RespValor Pop(string[] a, bool esquerda)
    {
        if (a.Length is < 2 or > 3) return ErroAridade(a[0]);
        var lista = Lista(a[1]);
        if (lista is null || lista.Count == 0) return a.Length == 3 ? new RespValor.Lista(null) : RespValor.Nulo;
        var quantos = a.Length == 3 ? (int)Positivo(Long(a[2])) : 1;
        var saida = new List<RespValor>();
        while (quantos-- > 0 && lista.Count > 0)
        {
            var no = esquerda ? lista.First! : lista.Last!;
            lista.Remove(no);
            saida.Add(RespValor.Bulk.De(no.Value));
        }
        if (lista.Count == 0) Banco.Remover(a[1]);
        return a.Length == 3 ? new RespValor.Lista(saida) : saida[0];
    }

    private RespValor LRange(string chave, long inicio, long fim)
    {
        var lista = Lista(chave);
        if (lista is null) return RespValor.ListaVazia;
        var n = lista.Count;
        if (inicio < 0) inicio = Math.Max(0, n + inicio);
        if (fim < 0) fim = n + fim;
        fim = Math.Min(fim, n - 1);
        if (inicio > fim) return RespValor.ListaVazia;
        return Strings(lista.Skip((int)inicio).Take((int)(fim - inicio + 1)));
    }

    private RespValor LIndex(string chave, long indice)
    {
        var lista = Lista(chave);
        if (lista is null) return RespValor.Nulo;
        if (indice < 0) indice += lista.Count;
        return indice < 0 || indice >= lista.Count ? RespValor.Nulo : RespValor.Bulk.De(lista.ElementAt((int)indice));
    }

    // ---- hashes ----

    private Dictionary<string, string>? Hash(string chave) => Banco.TentarObter(chave, out var e) ? e.Tipo == Tipo.Hash ? (Dictionary<string, string>)e.Valor : throw new TipoErradoException() : null;

    private RespValor HSet(string[] a)
    {
        if (a.Length < 4 || a.Length % 2 != 0) return ErroAridade("hset");
        var h = (Dictionary<string, string>)Banco.ObterOuCriar(a[1], Tipo.Hash, () => new Dictionary<string, string>(StringComparer.Ordinal)).Valor;
        var novos = 0;
        for (var k = 2; k < a.Length; k += 2) { if (h.TryAdd(a[k], a[k + 1])) novos++; else h[a[k]] = a[k + 1]; }
        return new RespValor.Inteiro(novos);
    }

    private RespValor HDel(string[] a)
    {
        if (a.Length < 3) return ErroAridade("hdel");
        var h = Hash(a[1]);
        if (h is null) return new RespValor.Inteiro(0);
        var removidos = a.Skip(2).Count(h.Remove);
        if (h.Count == 0) Banco.Remover(a[1]);
        return new RespValor.Inteiro(removidos);
    }

    private RespValor HIncrBy(string chave, string campo, long delta)
    {
        var h = (Dictionary<string, string>)Banco.ObterOuCriar(chave, Tipo.Hash, () => new Dictionary<string, string>(StringComparer.Ordinal)).Valor;
        var atual = h.TryGetValue(campo, out var s) ? long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException("hash value is not an integer") : 0;
        var novo = checked(atual + delta);
        h[campo] = novo.ToString(CultureInfo.InvariantCulture);
        return new RespValor.Inteiro(novo);
    }

    // ---- servidor ----

    private string Info()
    {
        var sb = new StringBuilder();
        sb.Append("# Server\r\n").Append("cofre_version:1.0.0\r\n").Append(CultureInfo.InvariantCulture, $"uptime_in_seconds:{(long)(Banco.Agora - InicioEm).TotalSeconds}\r\n");
        sb.Append("# Stats\r\n").Append(CultureInfo.InvariantCulture, $"total_commands_processed:{ComandosProcessados}\r\n")
          .Append(CultureInfo.InvariantCulture, $"expired_keys:{Banco.Expiracoes}\r\n").Append(CultureInfo.InvariantCulture, $"evicted_keys:{Banco.Despejos}\r\n");
        sb.Append("# Keyspace\r\n").Append(CultureInfo.InvariantCulture, $"db0:keys={Banco.Tamanho},maxkeys={Banco.MaximoDeChaves}\r\n");
        return sb.ToString();
    }

    // ---- utilitários ----

    private static RespValor? Aridade(string[] a, int exata) => a.Length == exata ? null : ErroAridade(a[0]);
    private static RespValor.Erro ErroAridade(string cmd) => new($"ERR wrong number of arguments for '{cmd.ToLowerInvariant()}' command");
    private static string Arg(string[] a, int k) => k < a.Length ? a[k] : throw new ArgumentException("syntax error");
    private static long Long(string s) => long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException("value is not an integer or out of range");
    private static long Positivo(long n) => n > 0 ? n : throw new ArgumentException("value must be positive");
    private static RespValor Strings(IEnumerable<string> itens) => new RespValor.Lista(itens.Select(s => (RespValor)RespValor.Bulk.De(s)).ToList());
}

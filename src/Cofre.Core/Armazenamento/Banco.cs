using System.Diagnostics.CodeAnalysis;

namespace Cofre.Core.Armazenamento;

public enum Tipo { String, Lista, Hash }

/// <summary>Uma entrada do banco: o valor tipado, a expiração opcional e o nó da lista LRU.</summary>
public sealed class Entrada
{
    public required object Valor { get; set; }
    public required Tipo Tipo { get; set; }
    public DateTimeOffset? ExpiraEm { get; set; }
    public LinkedListNode<string>? NoLru { get; set; }

    public bool Expirada(DateTimeOffset agora) => ExpiraEm is { } e && e <= agora;
}

public sealed class TipoErradoException() : Exception("WRONGTYPE Operation against a key holding the wrong kind of value");

/// <summary>
/// Banco chave-valor em memória, single-threaded por desenho (o servidor serializa os comandos numa fila, como o Redis).
/// Expiração passiva na leitura e ativa por amostragem, e despejo LRU quando o número de chaves passa do limite.
/// </summary>
public sealed class Banco
{
    private readonly Dictionary<string, Entrada> _dados = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new(); // início = mais recente
    private readonly IRelogio _relogio;

    public Banco(IRelogio? relogio = null, int maximoDeChaves = 0)
    {
        _relogio = relogio ?? new RelogioDoSistema();
        MaximoDeChaves = maximoDeChaves;
    }

    /// <summary>0 = sem limite.</summary>
    public int MaximoDeChaves { get; }
    public long Despejos { get; private set; }
    public long Expiracoes { get; private set; }
    public int Tamanho => _dados.Count;
    public DateTimeOffset Agora => _relogio.Agora;

    public bool TentarObter(string chave, [NotNullWhen(true)] out Entrada? entrada)
    {
        if (_dados.TryGetValue(chave, out entrada))
        {
            if (entrada.Expirada(_relogio.Agora))
            {
                RemoverInterno(chave, entrada);
                Expiracoes++;
                entrada = null;
                return false;
            }
            Tocar(entrada);
            return true;
        }
        return false;
    }

    public Entrada Obter(string chave, Tipo tipo)
    {
        if (!TentarObter(chave, out var e)) throw new KeyNotFoundException(chave);
        if (e.Tipo != tipo) throw new TipoErradoException();
        return e;
    }

    /// <summary>Obtém ou cria uma entrada do tipo pedido; lança se existir com outro tipo.</summary>
    public Entrada ObterOuCriar(string chave, Tipo tipo, Func<object> fabrica)
    {
        if (TentarObter(chave, out var e))
        {
            if (e.Tipo != tipo) throw new TipoErradoException();
            return e;
        }
        return Definir(chave, fabrica(), tipo);
    }

    public Entrada Definir(string chave, object valor, Tipo tipo, DateTimeOffset? expiraEm = null)
    {
        if (_dados.TryGetValue(chave, out var existente))
        {
            existente.Valor = valor;
            existente.Tipo = tipo;
            existente.ExpiraEm = expiraEm;
            Tocar(existente);
            return existente;
        }
        Despejar();
        var e = new Entrada { Valor = valor, Tipo = tipo, ExpiraEm = expiraEm };
        e.NoLru = _lru.AddFirst(chave);
        _dados[chave] = e;
        return e;
    }

    public bool Remover(string chave)
    {
        if (!_dados.TryGetValue(chave, out var e)) return false;
        var viva = !e.Expirada(_relogio.Agora);
        RemoverInterno(chave, e);
        return viva;
    }

    public bool Existe(string chave) => TentarObter(chave, out _);

    public bool DefinirExpiracao(string chave, DateTimeOffset? quando)
    {
        if (!TentarObter(chave, out var e)) return false;
        e.ExpiraEm = quando;
        return true;
    }

    /// <summary>TTL em milissegundos: -2 se não existe, -1 se não expira.</summary>
    public long TtlMs(string chave)
    {
        if (!TentarObter(chave, out var e)) return -2;
        if (e.ExpiraEm is null) return -1;
        return Math.Max(0, (long)(e.ExpiraEm.Value - _relogio.Agora).TotalMilliseconds);
    }

    public IEnumerable<string> Chaves(string padrao = "*")
    {
        var agora = _relogio.Agora;
        return _dados.Where(kv => !kv.Value.Expirada(agora) && Glob.Casa(padrao, kv.Key)).Select(kv => kv.Key).ToList();
    }

    public void Limpar()
    {
        _dados.Clear();
        _lru.Clear();
    }

    /// <summary>Expiração ativa: examina uma amostra e remove as vencidas. Devolve quantas removeu.</summary>
    public int ExpirarAmostra(int amostra = 20)
    {
        var agora = _relogio.Agora;
        var vencidas = _dados.Where(kv => kv.Value.Expirada(agora)).Take(amostra).Select(kv => kv.Key).ToList();
        foreach (var chave in vencidas) RemoverInterno(chave, _dados[chave]);
        Expiracoes += vencidas.Count;
        return vencidas.Count;
    }

    private void Tocar(Entrada e)
    {
        if (e.NoLru is { List: not null } no && no != _lru.First)
        {
            _lru.Remove(no);
            _lru.AddFirst(no);
        }
    }

    private void Despejar()
    {
        if (MaximoDeChaves <= 0) return;
        while (_dados.Count >= MaximoDeChaves && _lru.Last is { } ultimo)
        {
            RemoverInterno(ultimo.Value, _dados[ultimo.Value]);
            Despejos++;
        }
    }

    private void RemoverInterno(string chave, Entrada e)
    {
        _dados.Remove(chave);
        if (e.NoLru is { List: not null } no) _lru.Remove(no);
        e.NoLru = null;
    }
}

/// <summary>Padrões estilo KEYS: * ? e [abc].</summary>
public static class Glob
{
    public static bool Casa(string padrao, string texto) => Casa(padrao.AsSpan(), texto.AsSpan());

    private static bool Casa(ReadOnlySpan<char> p, ReadOnlySpan<char> t)
    {
        while (!p.IsEmpty)
        {
            switch (p[0])
            {
                case '*':
                    p = p[1..];
                    if (p.IsEmpty) return true;
                    for (var k = 0; k <= t.Length; k++) if (Casa(p, t[k..])) return true;
                    return false;
                case '?':
                    if (t.IsEmpty) return false;
                    p = p[1..]; t = t[1..];
                    break;
                case '[':
                {
                    var fim = p.IndexOf(']');
                    if (fim < 0 || t.IsEmpty) return false;
                    var conjunto = p[1..fim];
                    var nega = !conjunto.IsEmpty && conjunto[0] == '^';
                    if (nega) conjunto = conjunto[1..];
                    var achou = conjunto.IndexOf(t[0]) >= 0;
                    if (achou == nega) return false;
                    p = p[(fim + 1)..]; t = t[1..];
                    break;
                }
                case '\\' when p.Length > 1:
                    if (t.IsEmpty || t[0] != p[1]) return false;
                    p = p[2..]; t = t[1..];
                    break;
                default:
                    if (t.IsEmpty || t[0] != p[0]) return false;
                    p = p[1..]; t = t[1..];
                    break;
            }
        }
        return t.IsEmpty;
    }
}

using System.Buffers;
using System.Globalization;
using System.Text;

namespace Cofre.Core.Protocolo;

/// <summary>Valor do protocolo RESP2: string simples, erro, inteiro, bulk string (ou nulo) e array (ou nulo).</summary>
public abstract record RespValor
{
    public sealed record Simples(string Texto) : RespValor;
    public sealed record Erro(string Mensagem) : RespValor;
    public sealed record Inteiro(long Valor) : RespValor;
    public sealed record Bulk(byte[]? Bytes) : RespValor
    {
        public static Bulk De(string? s) => new(s is null ? null : Encoding.UTF8.GetBytes(s));
        public string? Texto => Bytes is null ? null : Encoding.UTF8.GetString(Bytes);
    }
    public sealed record Lista(IReadOnlyList<RespValor>? Itens) : RespValor;

    public static readonly Simples Ok = new("OK");
    public static readonly Simples Pong = new("PONG");
    public static readonly Bulk Nulo = new((byte[]?)null);
    public static readonly Lista ListaVazia = new(Array.Empty<RespValor>());
}

public sealed class RespException(string mensagem) : Exception(mensagem);

/// <summary>
/// Serialização e leitura incremental do RESP2, o protocolo do Redis: cada tipo tem um prefixo de um byte e as linhas
/// terminam em CRLF. O parser trabalha sobre <see cref="ReadOnlySequence{T}"/> e devolve falso quando faltam bytes,
/// para ser usado com System.IO.Pipelines sem copiar.
/// </summary>
public static class Resp
{
    private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

    public static byte[] Serializar(RespValor v)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Escrever(buffer, v);
        return buffer.WrittenSpan.ToArray();
    }

    public static void Escrever(IBufferWriter<byte> w, RespValor v)
    {
        switch (v)
        {
            case RespValor.Simples s: Linha(w, '+', s.Texto); break;
            case RespValor.Erro e: Linha(w, '-', e.Mensagem); break;
            case RespValor.Inteiro i: Linha(w, ':', i.Valor.ToString(CultureInfo.InvariantCulture)); break;
            case RespValor.Bulk { Bytes: null }: Linha(w, '$', "-1"); break;
            case RespValor.Bulk b:
                Linha(w, '$', b.Bytes!.Length.ToString(CultureInfo.InvariantCulture));
                w.Write(b.Bytes);
                w.Write(Crlf);
                break;
            case RespValor.Lista { Itens: null }: Linha(w, '*', "-1"); break;
            case RespValor.Lista l:
                Linha(w, '*', l.Itens!.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var item in l.Itens) Escrever(w, item);
                break;
            default: throw new RespException("tipo desconhecido");
        }
    }

    private static void Linha(IBufferWriter<byte> w, char prefixo, string texto)
    {
        var tamanho = 1 + Encoding.UTF8.GetByteCount(texto) + 2;
        var span = w.GetSpan(tamanho);
        span[0] = (byte)prefixo;
        var n = Encoding.UTF8.GetBytes(texto, span[1..]);
        span[1 + n] = (byte)'\r';
        span[2 + n] = (byte)'\n';
        w.Advance(3 + n);
    }

    /// <summary>
    /// Tenta ler um valor completo. Aceita também o "protocolo inline" (comando em texto puro separado por espaços),
    /// como o redis-cli e o telnet mandam.
    /// </summary>
    public static bool TentarLer(ref ReadOnlySequence<byte> buffer, out RespValor? valor)
    {
        valor = null;
        var reader = new SequenceReader<byte>(buffer);
        if (!Ler(ref reader, out valor)) return false;
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    private static bool Ler(ref SequenceReader<byte> r, out RespValor? valor)
    {
        valor = null;
        if (!r.TryPeek(out var prefixo)) return false;
        switch ((char)prefixo)
        {
            case '+': { r.Advance(1); if (!LerLinha(ref r, out var t)) return false; valor = new RespValor.Simples(t); return true; }
            case '-': { r.Advance(1); if (!LerLinha(ref r, out var t)) return false; valor = new RespValor.Erro(t); return true; }
            case ':': { r.Advance(1); if (!LerLinha(ref r, out var t)) return false; valor = new RespValor.Inteiro(Numero(t)); return true; }
            case '$':
            {
                r.Advance(1);
                if (!LerLinha(ref r, out var t)) return false;
                var n = Numero(t);
                if (n == -1) { valor = RespValor.Nulo; return true; }
                if (n < 0 || n > 512 * 1024 * 1024) throw new RespException("tamanho de bulk inválido");
                if (r.Remaining < n + 2) return false;
                var bytes = new byte[n];
                r.TryCopyTo(bytes);
                r.Advance(n);
                if (!r.IsNext(Crlf, advancePast: true)) throw new RespException("esperado CRLF após o bulk");
                valor = new RespValor.Bulk(bytes);
                return true;
            }
            case '*':
            {
                r.Advance(1);
                if (!LerLinha(ref r, out var t)) return false;
                var n = Numero(t);
                if (n == -1) { valor = new RespValor.Lista(null); return true; }
                if (n < 0 || n > 1024 * 1024) throw new RespException("tamanho de array inválido");
                var itens = new List<RespValor>((int)n);
                for (var k = 0; k < n; k++)
                {
                    if (!Ler(ref r, out var item)) return false;
                    itens.Add(item!);
                }
                valor = new RespValor.Lista(itens);
                return true;
            }
            default:
            {
                // inline: "SET a b\r\n"
                if (!LerLinha(ref r, out var linha)) return false;
                var partes = linha.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => (RespValor)RespValor.Bulk.De(p)).ToList();
                valor = new RespValor.Lista(partes);
                return true;
            }
        }
    }

    private static bool LerLinha(ref SequenceReader<byte> r, out string texto)
    {
        texto = "";
        if (!r.TryReadTo(out ReadOnlySequence<byte> linha, Crlf, advancePastDelimiter: true)) return false;
        texto = Encoding.UTF8.GetString(linha);
        return true;
    }

    private static long Numero(string t) => long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : throw new RespException($"número inválido: '{t}'");

    /// <summary>Monta um comando como array de bulk strings, o formato que os clientes enviam.</summary>
    public static RespValor.Lista Comando(params string[] partes) => new(partes.Select(p => (RespValor)RespValor.Bulk.De(p)).ToList());
}

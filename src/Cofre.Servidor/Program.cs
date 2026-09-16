using System.Net;
using Cofre.Core.Armazenamento;
using Cofre.Core.Comandos;
using Cofre.Core.Rede;

var porta = 6379;
var maximo = 0;
var endereco = IPAddress.Loopback;
for (var k = 0; k < args.Length; k++)
{
    switch (args[k])
    {
        case "--porta" or "-p": porta = int.Parse(args[++k]); break;
        case "--max-chaves" or "-m": maximo = int.Parse(args[++k]); break;
        case "--bind" or "-b": endereco = IPAddress.Parse(args[++k]); break;
        case "--ajuda" or "-h":
            Console.WriteLine("cofre [--porta 6379] [--max-chaves 0] [--bind 127.0.0.1]");
            return;
    }
}

var banco = new Banco(maximoDeChaves: maximo);
await using var servidor = new Servidor(new Executor(banco), endereco, porta);
servidor.Iniciar();
Console.WriteLine($"cofre-kv ouvindo em {endereco}:{servidor.Porta}" + (maximo > 0 ? $" (LRU, máximo de {maximo} chaves)" : ""));
Console.WriteLine("teste com: redis-cli -p " + servidor.Porta + "   ou   telnet localhost " + servidor.Porta);

var parar = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; parar.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => parar.TrySetResult();
await parar.Task;
Console.WriteLine("encerrando");

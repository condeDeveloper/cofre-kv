# Cofre KV

[![CI](https://github.com/condeDeveloper/cofre-kv/actions/workflows/ci.yml/badge.svg)](https://github.com/condeDeveloper/cofre-kv/actions/workflows/ci.yml)

Um banco chave-valor em memória compatível com o protocolo do Redis, escrito do zero em C# e .NET 8. O `redis-cli` conecta nele sem saber a diferença.

## O que tem dentro

- **RESP2** completo: string simples, erro, inteiro, bulk string e array, com nulos. Parser incremental sobre `ReadOnlySequence<byte>`, sem copiar, que devolve "faltam bytes" e é chamado de novo quando chega mais dado. Aceita também o protocolo inline, então `telnet` funciona.
- **Servidor TCP** com `System.IO.Pipelines`: uma tarefa por conexão lê e escreve, e **todos os comandos passam por um único laço de execução** (um `Channel`). Atomicidade sem travas, como o Redis. Pipelining de comandos funciona naturalmente.
- **Tipos**: strings com contadores (`INCR`, `INCRBY`, `APPEND`...), listas como deque (`LPUSH`, `RPOP`, `LRANGE` com índices negativos...) e hashes (`HSET`, `HGETALL`, `HINCRBY`...). `WRONGTYPE` ao misturar.
- **Expiração**: `SET ... EX/PX`, `EXPIRE`, `PEXPIRE`, `TTL`, `PTTL`, `PERSIST`. Passiva na leitura e ativa por amostragem a cada 100 ms, dentro do mesmo laço. Relógio injetável para testar sem esperar.
- **Despejo LRU**: com `--max-chaves N`, a chave menos usada sai quando entra uma nova. Lista duplamente ligada mais dicionário, O(1).
- **`KEYS`** com glob (`*`, `?`, `[abc]`, `[^a]`), `RENAME`, `TYPE`, `DBSIZE`, `FLUSHDB`, `INFO`.

## Rodar

```bash
dotnet run --project src/Cofre.Servidor -- --porta 6379 --max-chaves 10000
```

```
$ redis-cli -p 6379
127.0.0.1:6379> SET saudacao "olá" EX 60
OK
127.0.0.1:6379> INCR visitas
(integer) 1
127.0.0.1:6379> RPUSH fila a b c
(integer) 3
127.0.0.1:6379> LRANGE fila 0 -1
1) "a"
2) "b"
3) "c"
127.0.0.1:6379> HSET usuario:1 nome Ana idade 30
(integer) 2
127.0.0.1:6379> TTL saudacao
(integer) 57
```

Sem `redis-cli`, `telnet localhost 6379` e digite `PING`.

## Testes

```bash
dotnet test
```

Serialização e leitura do RESP com cortes em todas as posições, dois comandos no mesmo buffer, inline; cada família de comandos; expiração com relógio manual e expiração ativa; LRU despejando a menos usada; e o servidor real por TCP: pipelining de 100 comandos, oito clientes concorrentes incrementando o mesmo contador sem perder nada, erro de protocolo fechando a conexão.

## Arquitetura

```
src/Cofre.Core
  Protocolo/      Resp (serializador e parser incremental)
  Armazenamento/  Banco (dicionário + LRU + expiração), Relogio
  Comandos/       Executor (dialeto do Redis)
  Rede/           Servidor (TCP, Pipelines, laço único de execução)
src/Cofre.Servidor  executável
tests/Cofre.Tests
```

## Licença

MIT

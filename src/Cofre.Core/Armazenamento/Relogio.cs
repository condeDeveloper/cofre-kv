namespace Cofre.Core.Armazenamento;

/// <summary>Fonte de tempo injetável para testar expiração sem esperar.</summary>
public interface IRelogio
{
    DateTimeOffset Agora { get; }
}

public sealed class RelogioDoSistema : IRelogio
{
    public DateTimeOffset Agora => DateTimeOffset.UtcNow;
}

public sealed class RelogioManual(DateTimeOffset inicio) : IRelogio
{
    public DateTimeOffset Agora { get; private set; } = inicio;
    public void Avancar(TimeSpan delta) => Agora += delta;
}

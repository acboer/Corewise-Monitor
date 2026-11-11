namespace HwMon.Core.Collection.Bench;

/// <summary>
/// Falha numa thread de trabalho dos testes. Exceção solta numa <see cref="Thread"/> encerra o processo inteiro (no
/// caso, o serviço): cada thread passa pelo <see cref="Wrap"/>, que guarda a primeira falha e avisa as outras para
/// pararem; quem criou as threads espera todas e chama <see cref="ThrowIfFaulted"/>.
/// </summary>
internal sealed class WorkerFault
{
    private Exception? _error;

    public Exception? Error => Volatile.Read(ref _error);

    public ThreadStart Wrap(Action body, Action stopOthers) => () =>
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _error, ex, null);
            try { stopOthers(); } catch (Exception) { /* já está parando */ }
        }
    };

    public void ThrowIfFaulted()
    {
        if (Error is { } e) throw new InvalidOperationException("Uma das threads do teste falhou: " + e.Message, e);
    }
}

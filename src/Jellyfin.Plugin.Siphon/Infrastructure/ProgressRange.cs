namespace Jellyfin.Plugin.Siphon.Infrastructure;

/// <summary>Maps a stage into native task progress without queued or out-of-order reports.</summary>
internal sealed class ProgressRange(IProgress<double>? target, double start, double end) : IProgress<double>
{
    private readonly Lock _gate = new();
    private double _last = -1;

    public void Report(double value)
    {
        if (target is null) return;
        var mapped = start + Math.Clamp(value, 0, 100) * (end - start) / 100;
        lock (_gate)
        {
            if (mapped <= _last) return;
            _last = mapped;
            target.Report(mapped);
        }
    }
}

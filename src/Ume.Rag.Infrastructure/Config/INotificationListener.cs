namespace Ume.Rag.Infrastructure.Config;

/// <summary>
/// Another Postgres <c>LISTEN</c> channel carried on the <see cref="ConfigVersionMonitor"/>'s connection (issue #21), so a
/// process keeps the one listening connection data-model §1.1 budgets (e.g. the worker's <c>rag_jobs</c>). Register as a
/// singleton. <see cref="OnNotification"/> runs on the monitor's connection thread: keep it short (signal, never block).
/// </summary>
public interface INotificationListener
{
    /// <summary>The channel: lower-case letters, digits and underscores (it is put into the <c>LISTEN</c> statement).</summary>
    string Channel { get; }

    void OnNotification(string payload);
}

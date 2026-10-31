using System.Diagnostics.Metrics;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Malware;

namespace Ume.Rag.Worker.Connectors;

/// <summary>
/// Connector metrics of connectors design §16 on meter <c>Ume.Rag</c>. Dimensions stay low-cardinality: never host names,
/// URLs, file names or titles.
/// </summary>
public sealed class ConnectorMetrics
{
    private readonly Counter<long> _egressBlocked;
    private readonly Histogram<double> _scanDuration;
    private readonly Counter<long> _detections;
    private readonly Gauge<double> _signatureAge;

    public ConnectorMetrics(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        var meter = meters.Create(Microsoft.Extensions.Hosting.Extensions.TelemetryName);
        _egressBlocked = meter.CreateCounter<long>("rag.egress.blocked", description: "Requests the egress guard refused, by reason and purpose.");
        _scanDuration = meter.CreateHistogram<double>("rag.clamav.scan.duration", "s", "ClamAV INSTREAM scans by origin and result.");
        _detections = meter.CreateCounter<long>("rag.clamav.detections", description: "ClamAV findings by origin.");
        _signatureAge = meter.CreateGauge<double>("rag.clamav.signature_age_hours", "h", "Age of the daily signature database (zVERSION).");
    }

    public void EgressBlocked(string reason, EgressPurpose purpose) =>
        _egressBlocked.Add(1, new("reason", reason), new("purpose", purpose.ToString().ToLowerInvariant()));

    public void ClamAvScanned(string origin, TimeSpan elapsed, ScanResult result)
    {
        var name = result switch
        {
            ScanResult.CleanResult => "clean",
            ScanResult.Malware => "malware",
            ScanResult.TooLarge => "too_large",
            _ => "unavailable",
        };
        _scanDuration.Record(elapsed.TotalSeconds, new("origin", origin), new("result", name));
        if (result is ScanResult.Malware)
        {
            _detections.Add(1, new KeyValuePair<string, object?>("origin", origin));
        }
    }

    public void SignatureAge(TimeSpan age) => _signatureAge.Record(age.TotalHours);
}

using System.ComponentModel.DataAnnotations;

namespace Kalla.Infrastructure.Config;

/// <summary>Deployment settings of the config-version library (<c>Config:*</c>, data-model §5).</summary>
public sealed class ConfigOptions
{
    public const string SectionName = "Config";

    /// <summary>
    /// Grace window: security-relevant decisions use the last confirmed snapshot for at most this long without a
    /// confirmation from the database, then are refused with 503 <c>config_unconfirmed</c> (identity design §4.5).
    /// </summary>
    [Range(1, 3600)]
    public int MaxUnconfirmedSeconds { get; set; } = 90;
}

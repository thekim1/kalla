using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.TestKit;

/// <summary>
/// A test host whose gateway clients go to a fake or a stub handler that does not answer <c>GET /v1/ume/context</c> as the
/// service's own credential key would: the credential key's residency ceiling is known from the start, as if the context
/// had been read (M-17 C9; production starts at <c>OnPrem</c> and takes it from the context, see <see cref="GatewayCeiling"/>).
/// Call after the service registers its gateway clients (in <c>ConfigureTestServices</c>, or after <c>AddGatewayClient</c>).
/// </summary>
public static class GatewayTestCeiling
{
    public static IServiceCollection UseGatewayCredentialCeiling(this IServiceCollection services, Residency maximum = Residency.External)
    {
        ArgumentNullException.ThrowIfNull(services);
        var features = new GatewayFeatures();
        features.UpdateCredentialResidencies([.. Enum.GetValues<Residency>().Where(r => r <= maximum).Select(ResidencyCeiling.ToHeader)]);
        services.RemoveAll<GatewayFeatures>();
        services.AddSingleton(features);
        return services;
    }
}

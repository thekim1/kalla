using System.Data.Common;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Kalla.Retrieval.Postgres;

namespace Kalla.Api.Tests;

/// <summary>
/// kalla-api has one connection pool (data-model §1.1, knowledge §6.6; B-21): EF, the config monitor and retrieval share the
/// <c>ragdb</c> data source, so <c>Retrieval:MaxConcurrentSearches</c> = ⌊(pool − 3) ÷ 2⌋ holds for the whole process.
/// </summary>
public sealed class DataSourceTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    [Fact]
    public void The_api_registers_one_data_source_with_retrievals_auto_prepare()
    {
        IServiceCollection? registered = null;
        using var api = db.ApiHost().WithWebHostBuilder(web => web.ConfigureTestServices(services => registered = services));
        var dataSource = api.Services.GetRequiredService<NpgsqlDataSource>();

        registered!.Where(d => typeof(DbDataSource).IsAssignableFrom(d.ServiceType)).ShouldHaveSingleItem().IsKeyedService.ShouldBeFalse();
        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        settings.MaxAutoPrepare.ShouldBe(RetrievalDataSource.MaxAutoPrepare);
        settings.AutoPrepareMinUsages.ShouldBe(RetrievalDataSource.AutoPrepareMinUsages);
    }
}

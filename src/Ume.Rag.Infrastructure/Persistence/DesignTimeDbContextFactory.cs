using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ume.Rag.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> at design time; no real database connection is made.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RagDbContext>
{
    public RagDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RagDbContext>()
            .UseNpgsql("Host=localhost;Database=ragdb;Username=design;Password=design", RagDbContext.ConfigureNpgsql)
            .Options;
        return new RagDbContext(options);
    }
}

using Dispatch.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dispatch.Data.Postgres;

/// <summary>
/// Lets `dotnet ef migrations add` build a DispatchDbContext for this provider without running the app.
/// The connection string is never opened during scaffolding — EF only needs the provider to know which
/// SQL to generate — so a placeholder is correct here and deliberately not a real endpoint.
/// </summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DispatchDbContext>
{
    public DispatchDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql("Host=localhost;Port=55432;Database=efprobe3;Username=postgres;Password=testpass",
                o => o.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.GetName().Name))
            .Options);
}

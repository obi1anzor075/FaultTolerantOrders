using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderService.Api;

// EF tooling can generate migrations without starting the HTTP server or its background worker.
public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Orders")
            ?? "Host=localhost;Port=5433;Database=orders;Username=lab;Password=labpass";
        return new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(connection).Options);
    }
}

using Microsoft.EntityFrameworkCore;

namespace OrderService.Api;

public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Total).HasPrecision(18, 2);
            entity.HasMany(order => order.Items).WithOne().HasForeignKey(item => item.OrderId);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.UnitPrice).HasPrecision(18, 2);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Type).HasMaxLength(256);
            entity.Property(message => message.Payload).HasColumnType("jsonb");
            entity.Property(message => message.Error).HasMaxLength(2000);
            entity.HasOne<Order>().WithMany().HasForeignKey(message => message.OrderId);
            entity.HasIndex(message => new { message.OrderId, message.Type }).IsUnique();
            entity.HasIndex(message => new { message.NextRetryAt, message.OccurredAt })
                .HasFilter("\"PublishedAt\" IS NULL");
        });
    }
}

public sealed class Order
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public decimal Total { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public sealed class OrderItem
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

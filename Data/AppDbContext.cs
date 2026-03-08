using Microsoft.EntityFrameworkCore;
using EatHealthyCycle.Models;

namespace EatHealthyCycle.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Dieta> Dietas => Set<Dieta>();
    public DbSet<DietaDia> DietaDias => Set<DietaDia>();
    public DbSet<Comida> Comidas => Set<Comida>();
    public DbSet<Alimento> Alimentos => Set<Alimento>();
    public DbSet<PlanSemanal> PlanesSemanal => Set<PlanSemanal>();
    public DbSet<PlanDia> PlanDias => Set<PlanDia>();
    public DbSet<PlanComida> PlanComidas => Set<PlanComida>();
    public DbSet<RegistroPeso> RegistrosPeso => Set<RegistroPeso>();
    public DbSet<ItemListaCompra> ItemsListaCompra => Set<ItemListaCompra>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // --- Usuario (Auth) ---
        modelBuilder.Entity<Usuario>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Username).IsRequired().HasMaxLength(100);
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Email).IsRequired().HasMaxLength(200);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.PasswordHash).IsRequired();
            e.Property(u => u.Role).HasConversion<int>();
            e.Property(u => u.RefreshToken).HasMaxLength(256);
            e.Property(u => u.IsActive).HasDefaultValue(true);
            e.Property(u => u.ActivationToken).HasMaxLength(128);
            e.Property(u => u.TwoFactorEnabled).HasDefaultValue(false);
            e.Property(u => u.TwoFactorSecret).HasMaxLength(128);
            e.Property(u => u.RecoveryCodes).HasMaxLength(1024);

            // Seed SuperUserMaster admin
            e.HasData(new Usuario
            {
                Id = 1,
                Username = "admin",
                Nombre = "Admin",
                Email = "admin@eathealthycycle.local",
                PasswordHash = "$2a$11$r1zN2HmMy2FnebH4onffcOzWj8IsqmrB0Yxe5k1VgbPzXOh29WGDm", // Admin123!
                Role = UserRole.SuperUserMaster,
                IsActive = true,
                FechaCreacion = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        // --- Dieta ---
        modelBuilder.Entity<RegistroPeso>()
            .Property(r => r.Peso)
            .HasPrecision(5, 2);

        modelBuilder.Entity<RegistroPeso>()
            .HasIndex(r => new { r.UsuarioId, r.Fecha })
            .IsUnique();

        modelBuilder.Entity<Comida>()
            .Property(c => c.Tipo)
            .HasConversion<string>();

        modelBuilder.Entity<PlanComida>()
            .Property(c => c.Tipo)
            .HasConversion<string>();
    }
}

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

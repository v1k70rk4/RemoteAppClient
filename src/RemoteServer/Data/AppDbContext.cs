using Microsoft.EntityFrameworkCore;
using RemoteServer.Data.Entities;

namespace RemoteServer.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceTelemetry> DeviceTelemetry => Set<DeviceTelemetry>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<ReleasePackage> ReleasePackages => Set<ReleasePackage>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<Role>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<UserRole>(e =>
        {
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Role).WithMany(r => r.UserRoles).HasForeignKey(x => x.RoleId);
        });

        b.Entity<Device>(e =>
        {
            e.HasIndex(x => x.DeviceId).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.TunnelPort).IsUnique(); // egyedi bástya-port (NULL többször is lehet)
            e.HasOne(x => x.Group).WithMany(g => g.Devices).HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<DeviceTelemetry>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.CollectedAt });
            e.Property(x => x.PayloadJson).HasColumnType("json");
        });

        b.Entity<EnrollmentToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<Command>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.Status });
            e.Property(x => x.PayloadJson).HasColumnType("json");
            e.Property(x => x.ResultJson).HasColumnType("json");
        });

        b.Entity<ReleasePackage>(e =>
        {
            e.HasIndex(x => new { x.Channel, x.Component, x.UploadedAt });
        });

        b.Entity<RemoteSession>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.OpenedAt });
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.DetailJson).HasColumnType("json");
        });
    }
}

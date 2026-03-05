using Microsoft.EntityFrameworkCore;
using IgAiBackend.Models;

namespace IgAiBackend.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        // 基礎設定表
        public DbSet<Platform> Platforms { get; set; }
        public DbSet<AccountType> AccountTypes { get; set; }
        public DbSet<TargetPerson> TargetPersons { get; set; }
        public DbSet<SocialAccount> SocialAccounts { get; set; }
        public DbSet<SystemAlert> SystemAlerts { get; set; }

        // 核心資料表
        public DbSet<MediaAsset> MediaAssets { get; set; }
        public DbSet<AiAnalysisLog> AiAnalysisLogs { get; set; }

        // 🔐 權限管理核心表 (本次重點)
        public DbSet<User> Users { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<SystemRoute> SystemRoutes { get; set; }
        public DbSet<RolePermission> RolePermissions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // =================================================
            // 🌟 1. 設定 RolePermission 複合主鍵 (修正重點)
            // =================================================
            modelBuilder.Entity<RolePermission>()
                .HasKey(rp => new { rp.RoleId, rp.RouteId });

            modelBuilder.Entity<RolePermission>()
                .HasOne(rp => rp.SystemRoute)
                .WithMany(sr => sr.RolePermissions)
                .HasForeignKey(rp => rp.RouteId);

            // A. 先建立角色
            modelBuilder.Entity<Role>().HasData(
                new Role { Id = 1, Code = "Admin", Name = "管理員" },
                new Role { Id = 2, Code = "Reviewer", Name = "覆核員" },
                new Role { Id = 3, Code = "Guest", Name = "訪客" }
            );

            // B. 建立路由定義
            modelBuilder.Entity<SystemRoute>().HasData(
                new SystemRoute { Id = 1, RouteName = "HitlDashboard", Path = "/", Title = "HITL 人工覆核中心", Icon = "🎯", IsPublic = true },
                new SystemRoute { Id = 2, RouteName = "SystemMonitor", Path = "/monitor", Title = "系統監控大盤", Icon = "📈", IsPublic = true },
                new SystemRoute { Id = 3, RouteName = "ColdStartSetup", Path = "/cold-start", Title = "冷啟動建檔", Icon = "❄️", IsPublic = false },
                new SystemRoute { Id = 4, RouteName = "ProfileManager", Path = "/profile-manager", Title = "追蹤人物管理", Icon = "👤", IsPublic = false },
                new SystemRoute { Id = 5, RouteName = "UserManagement", Path = "/user-management", Title = "帳號權限管理", Icon = "🔐", IsPublic = false },
                new SystemRoute { Id = 6, RouteName = "ClassifiedResults", Path = "/classified", Title = "分類結果查看", Icon = "🖼️", IsPublic = false }
            );

            // C. 建立管理員帳號 (注意：這裡要用 RoleId = 1)
            modelBuilder.Entity<User>().HasData(
                new User
                {
                    Id = 1,
                    Username = "admin",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("123456"),
                    RoleId = 1, // 🌟 這裡修正：由 Role 改為 RoleId
                    IsActive = true,
                    CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            );

            // D. 建立權限關聯 (讓 Admin 擁有所有非公開頁面權限)
            modelBuilder.Entity<RolePermission>().HasData(
                new RolePermission { RoleId = 1, RouteId = 1, CanView = true, CanCreate = true, CanUpdate = true, CanDelete = true }, // HITL
                new RolePermission { RoleId = 1, RouteId = 2, CanView = true, CanCreate = true, CanUpdate = true, CanDelete = true }, // 🌟 補上這行 (系統監控)
                new RolePermission { RoleId = 1, RouteId = 3, CanView = true, CanCreate = true, CanUpdate = true, CanDelete = true }, // 冷啟動
                new RolePermission { RoleId = 1, RouteId = 4, CanView = true, CanCreate = true, CanUpdate = true, CanDelete = true }, // 人物管理
                new RolePermission { RoleId = 1, RouteId = 5, CanView = true, CanCreate = true, CanUpdate = true, CanDelete = true },  // 權限管理
                new RolePermission { RoleId = 1, RouteId = 6, CanView = true, CanCreate = true, CanUpdate = true, CanDelete = true } // 分類結果
            );
            modelBuilder.Entity<RolePermission>().HasData(
                // 覆核員可以進「人工覆核中心」，可以執行「覆核疊加」，但不能「刪除樣本」
                new RolePermission
                {
                    RoleId = 2,
                    RouteId = 1,
                    CanView = true,
                    CanCreate = false,
                    CanUpdate = true,
                    CanDelete = false
                },
                // 覆核員可以進「冷啟動建檔」，可以執行「特徵確認」，但不能「刪除/排除」
                new RolePermission
                {
                    RoleId = 2,
                    RouteId = 3,
                    CanView = true,
                    CanCreate = false,
                    CanUpdate = true,
                    CanDelete = false
                }
            );
            modelBuilder.Entity<RolePermission>().HasData(
        // 訪客對「人工覆核中心」與「系統監控」只有 CanView 權限
        new RolePermission
        {
            RoleId = 3,
            RouteId = 1,
            CanView = true,
            CanCreate = false,
            CanUpdate = false,
            CanDelete = false
        },
        new RolePermission
        {
            RoleId = 3,
            RouteId = 2,
            CanView = true,
            CanCreate = false,
            CanUpdate = false,
            CanDelete = false
        }
    );

            // =================================================
            // 3. 設定 MediaAsset 與其他 (維持原樣)
            // =================================================
            modelBuilder.Entity<MediaAsset>(entity =>
            {
                entity.HasIndex(m => new { m.FileName, m.SystemName });
                entity.HasIndex(m => m.SystemName);
            });

            modelBuilder.Entity<AiAnalysisLog>(entity =>
            {
                entity.HasIndex(l => l.RecognitionStatus);
                entity.HasOne(l => l.MediaAsset)
                      .WithMany()
                      .HasForeignKey(l => l.MediaId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<SocialAccount>(entity =>
            {
                entity.HasIndex(s => new { s.PersonId, s.PlatformId, s.AccountIdentifier }).IsUnique();
            });
        }
    }
}
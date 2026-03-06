using Microsoft.EntityFrameworkCore;
using IgAiBackend.Models;
using System.Collections.Generic;

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
        public DbSet<SysStatus> SysStatuses { get; set; }
        public DbSet<SysAction> SysActions { get; set; } // 🌟 新增：系統動作字典表

        // 核心資料表
        public DbSet<MediaAsset> MediaAssets { get; set; }
        public DbSet<AiAnalysisLog> AiAnalysisLogs { get; set; }

        // 🔐 權限管理核心表
        public DbSet<User> Users { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<SystemRoute> SystemRoutes { get; set; }
        public DbSet<RolePermission> RolePermissions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // =================================================
            // 🌟 1. 設定 RolePermission 關聯與唯一約束
            // =================================================
            // 廢除原本的 (RoleId, RouteId) 主鍵，改為確保「同一角色在同一路由的同一動作」不重複
            modelBuilder.Entity<RolePermission>()
                .HasIndex(rp => new { rp.RoleId, rp.RouteId, rp.ActionId })
                .IsUnique();

            modelBuilder.Entity<RolePermission>()
                .HasOne(rp => rp.SystemRoute)
                .WithMany(sr => sr.RolePermissions)
                .HasForeignKey(rp => rp.RouteId);

            // =================================================
            // 🌟 2. 寫入基礎字典資料
            // =================================================
            modelBuilder.Entity<Role>().HasData(
                new Role { Id = 1, Code = "Admin", Name = "管理員" },
                new Role { Id = 2, Code = "Reviewer", Name = "覆核員" },
                new Role { Id = 3, Code = "Guest", Name = "訪客" }
            );

            // 寫入基礎動作 (Actions)
            modelBuilder.Entity<SysAction>().HasData(
                new SysAction { Id = 1, Code = "VIEW", DisplayName = "檢視" },
                new SysAction { Id = 2, Code = "CREATE", DisplayName = "新增" },
                new SysAction { Id = 3, Code = "UPDATE", DisplayName = "修改" },
                new SysAction { Id = 4, Code = "DELETE", DisplayName = "刪除" },
                new SysAction { Id = 5, Code = "APPROVE", DisplayName = "覆核" }
            );

            modelBuilder.Entity<SystemRoute>().HasData(
                new SystemRoute { Id = 1, RouteName = "HitlDashboard", Path = "/", Title = "HITL 人工覆核中心", Icon = "🎯", IsPublic = true },
                new SystemRoute { Id = 2, RouteName = "SystemMonitor", Path = "/monitor", Title = "系統監控大盤", Icon = "📈", IsPublic = true },
                new SystemRoute { Id = 3, RouteName = "ColdStartSetup", Path = "/cold-start", Title = "冷啟動建檔", Icon = "❄️", IsPublic = false },
                new SystemRoute { Id = 4, RouteName = "ProfileManager", Path = "/profile-manager", Title = "追蹤人物管理", Icon = "👤", IsPublic = false },
                new SystemRoute { Id = 5, RouteName = "UserManagement", Path = "/user-management", Title = "帳號權限管理", Icon = "🔐", IsPublic = false },
                new SystemRoute { Id = 6, RouteName = "ClassifiedResults", Path = "/classified", Title = "分類結果查看", Icon = "🖼️", IsPublic = false }
            );

            modelBuilder.Entity<User>().HasData(
                new User
                {
                    Id = 1,
                    Username = "admin",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("123456"),
                    RoleId = 1, 
                    IsActive = true,
                    CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            );

            // =================================================
            // 🌟 3. 轉換舊版權限，以新版多筆紀錄 (ActionId) 的方式寫入
            // =================================================
            var rolePermissions = new List<RolePermission>();
            int rpId = 1;

            // D1. 管理員 (Admin) - 擁有全部頁面的全部權限
            for (int route = 1; route <= 6; route++)
            {
                for (int action = 1; action <= 5; action++) // 1~5 包含 APPROVE
                {
                    rolePermissions.Add(new RolePermission { Id = rpId++, RoleId = 1, RouteId = route, ActionId = action });
                }
            }

            // D2. 覆核員 (Reviewer)
            // 路由1 (人工覆核): View, Update
            rolePermissions.Add(new RolePermission { Id = rpId++, RoleId = 2, RouteId = 1, ActionId = 1 }); // VIEW
            rolePermissions.Add(new RolePermission { Id = rpId++, RoleId = 2, RouteId = 1, ActionId = 3 }); // UPDATE
            // 路由3 (冷啟動): View, Update
            rolePermissions.Add(new RolePermission { Id = rpId++, RoleId = 2, RouteId = 3, ActionId = 1 }); // VIEW
            rolePermissions.Add(new RolePermission { Id = rpId++, RoleId = 2, RouteId = 3, ActionId = 3 }); // UPDATE

            // D3. 訪客 (Guest)
            // 路由1 (人工覆核): View
            rolePermissions.Add(new RolePermission { Id = rpId++, RoleId = 3, RouteId = 1, ActionId = 1 }); // VIEW
            // 路由2 (系統監控): View
            rolePermissions.Add(new RolePermission { Id = rpId++, RoleId = 3, RouteId = 2, ActionId = 1 }); // VIEW

            modelBuilder.Entity<RolePermission>().HasData(rolePermissions);

            // =================================================
            // 4. 設定 MediaAsset 與其他
            // =================================================
            modelBuilder.Entity<MediaAsset>(entity =>
            {
                entity.HasIndex(m => new { m.FileName, m.SystemName });
                entity.HasIndex(m => m.SystemName);
            });

            modelBuilder.Entity<AiAnalysisLog>(entity =>
            {
                entity.HasIndex(l => l.StatusId); 
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
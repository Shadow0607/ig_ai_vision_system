using DotNetEnv;
using IgAiBackend.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Minio;
using IgAiBackend.Hubs;
using IgAiBackend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using System.Text;
// 🌟 1. 新增引入反向代理標頭處理的命名空間
using Microsoft.AspNetCore.HttpOverrides; 

var builder = WebApplication.CreateBuilder(args);

// ====================================================
// 2. 智慧型載入 .env (地毯式往上搜尋)
// ====================================================
static string? FindEnvFile(string startPath)
{
    var dir = new DirectoryInfo(startPath);
    while (dir != null)
    {
        string path = Path.Combine(dir.FullName, ".env");
        if (File.Exists(path)) return path;
        dir = dir.Parent;
    }
    return null;
}

try 
{
    string? envPath = FindEnvFile(Directory.GetCurrentDirectory());
    if (envPath != null)
    {
        Console.WriteLine($"✅ [ENV] 載入設定檔：{envPath}");
        Env.Load(envPath);
    }
    else
    {
        Console.WriteLine("⚠️ [ENV] 警告：找不到 .env 檔案！將使用系統環境變數。");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"❌ [ENV] 載入失敗: {ex.Message}");
}

// ====================================================
// 🌟 3. 動態 Port 綁定 (不再寫死 5000)
// ====================================================
var listenPort = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://*:{listenPort}");

// 讀取變數
var dbHost = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
var dbPort = Environment.GetEnvironmentVariable("DB_PORT") ?? "3306";
var dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "ig_ai_system";
var dbUser = Environment.GetEnvironmentVariable("DB_USER") ?? "root";
var dbPass = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";

var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
var redisPort = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";
var redisPass = Environment.GetEnvironmentVariable("REDIS_PASSWORD"); 

// ====================================================
// 4. 設定 MySQL
// ====================================================
var connectionString = $"Server={dbHost};Port={dbPort};Database={dbName};User={dbUser};Password={dbPass};";

try
{
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
    );
    Console.WriteLine($"✅ [DB] 設定完成 ({dbHost})");
}
catch(Exception ex)
{
    Console.WriteLine($"❌ [DB] 設定失敗: {ex.Message}");
}

// ====================================================
// 5. 設定 Redis (NAS)
// ====================================================
var redisOptions = new ConfigurationOptions
{
    EndPoints = { $"{redisHost}:{redisPort}" },
    AbortOnConnectFail = false,
    ConnectTimeout = 3000
};

if (!string.IsNullOrEmpty(redisPass)) redisOptions.Password = redisPass;

builder.Services.AddSingleton<IConnectionMultiplexer>(sp => {
    try {
        var muxer = ConnectionMultiplexer.Connect(redisOptions);
        Console.WriteLine($"✅ [Redis] 連線成功 ({redisHost})");
        return muxer;
    } catch (Exception ex) {
        Console.WriteLine($"❌ [Redis] 連線失敗: {ex.Message}");
        return null!; 
    }
});

// ====================================================
// 🌟 5.5 反向代理標頭設定 (Nginx / API Gateway 必備)
// ====================================================
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // 允許接收來自反向代理的真實 IP (X-Forwarded-For) 與 HTTP/HTTPS 狀態 (X-Forwarded-Proto)
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // 清空預設限制，信任所有的本地代理轉發 (如果是 Docker 或同主機 Nginx 必須加這兩行)
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// ====================================================
// 🌟 6. 動態 CORS 設定 (從 .env 讀取白名單)
// ====================================================
var corsOriginsStr = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS") ?? "http://localhost:5173,http://127.0.0.1:5173";
var corsOrigins = corsOriginsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowVueApp", policy =>
    {
        policy.WithOrigins(corsOrigins) 
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddEndpointsApiExplorer();

// ====================================================
// 6.5 設定 Swagger (支援 JWT 輸入)
// ====================================================
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "請在下方輸入 Bearer {您的 JWT Token}",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement()
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

// ====================================================
// 6.6 註冊 JWT 驗證服務
// ====================================================
var publicKeyPem = Environment.GetEnvironmentVariable("JWT_PUBLIC_KEY")?.Replace("\\n", "\n");
if (string.IsNullOrWhiteSpace(publicKeyPem))
{
    Console.WriteLine("🚨 [嚴重錯誤] 缺少 JWT_PUBLIC_KEY，無法啟動驗證服務！");
    Environment.Exit(1);
}

var rsaPublic = RSA.Create();
rsaPublic.ImportFromPem(publicKeyPem);
var securityPublicKey = new RsaSecurityKey(rsaPublic);

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "IgAiSystem",
        ValidAudience = "IgAiSystemFrontend",
        IssuerSigningKey = securityPublicKey,
        ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 } 
    };

    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Cookies["auth_token"];
            if (!string.IsNullOrEmpty(token))
            {
                context.Token = token;
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddSignalR();
builder.Services.AddHostedService<RedisMonitorService>();
builder.Services.AddHostedService<OrphanFileSweeperService>();

// ====================================================
// 🌟 註冊 MinIO 服務 (改為直接讀取環境變數)
// ====================================================
var minioEndpoint = Environment.GetEnvironmentVariable("S3_ENDPOINT_URL") ?? "localhost:9000";
var minioAccessKey = Environment.GetEnvironmentVariable("S3_ACCESS_KEY");
var minioSecretKey = Environment.GetEnvironmentVariable("S3_SECRET_KEY");
var minioUseSsl = Environment.GetEnvironmentVariable("S3_USE_SSL")?.ToLower() == "true";

if (string.IsNullOrEmpty(minioAccessKey) || string.IsNullOrEmpty(minioSecretKey))
{
    Console.WriteLine("🚨 [警告] MinIO 帳號或密碼未設定，部分功能可能失效！");
}

builder.Services.AddMinio(configureClient => configureClient
    .WithEndpoint(minioEndpoint)
    .WithCredentials(minioAccessKey, minioSecretKey) // 👈 直接傳入環境變數
    .WithSSL(minioUseSsl)
    .Build());

var app = builder.Build();

// ====================================================
// 🌟 7. 啟動流程 (注意 Middleware 順序非常重要)
// ====================================================

// 🌟 最先套用：解析 Nginx 等反向代理轉發的真實 IP 與協定
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 套用 CORS 白名單
app.UseCors("AllowVueApp");

app.UseAuthentication(); 
app.UseAuthorization();

app.MapControllers();
app.MapHub<MonitorHub>("/hubs/monitor");

Console.WriteLine($"🚀 後端伺服器啟動中... 監聽 Port: {listenPort}");
Console.WriteLine($"👉 Swagger 文件: http://localhost:{listenPort}/swagger");

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<IgAiBackend.Data.ApplicationDbContext>();
    var redis = scope.ServiceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
    var dbRedis = redis.GetDatabase();

    var statuses = dbContext.SysStatuses.ToList();
    if (statuses.Any())
    {
        // 建立 HashEntry 陣列，格式為 "CATEGORY:CODE" -> ID
        var hashEntries = statuses
            .Select(s => new StackExchange.Redis.HashEntry($"{s.Category}:{s.Code}", s.Id))
            .ToArray();

        // 確保覆蓋舊快取
        dbRedis.KeyDelete("sys:statuses");
        dbRedis.HashSet("sys:statuses", hashEntries);
        
        Console.WriteLine($"🚀 [Redis Cache] 系統狀態字典已成功載入 Redis Hash (共 {hashEntries.Length} 筆)，Python 微服務可隨時拉取。");
    }
}

app.Run(); // 這是原本 Program.cs 的最後一行
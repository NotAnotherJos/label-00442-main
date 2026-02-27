// ============================================================================
// Web Scraper 通用框架 - .NET 9 + Mini API + EF Core + SQLite
// 所有代码整合在单一文件中（遵循 Prompt 约束）
// ============================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using HtmlAgilityPack;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Serilog;
using Serilog.Events;

// ============================================================================
// Serilog 日志配置
// ============================================================================
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithThreadId()
    .Enrich.WithProperty("Application", "WebScraper")
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/scraper-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
        shared: true)
    .CreateLogger();

try
{
    Log.Information("Starting Web Scraper application...");

    var builder = WebApplication.CreateBuilder(args);

    // 使用 Serilog
    builder.Host.UseSerilog();

    // ============================================================================
    // 服务配置
    // ============================================================================
    builder.Services.AddDbContext<ScraperDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") 
            ?? "Data Source=scraper.db"));

    builder.Services.AddScoped<IScraperService, ScraperService>();
    builder.Services.AddHttpClient<IScraperService, ScraperService>(client =>
    {
        client.DefaultRequestHeaders.Add("User-Agent", 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "Web Scraper API", Version = "v1" });
    });

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        });
    });

    var app = builder.Build();

    // ============================================================================
    // 中间件配置
    // ============================================================================
    app.UseCors();

    // Serilog 请求日志
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.GetLevel = (httpContext, elapsed, ex) => ex != null
            ? LogEventLevel.Error
            : httpContext.Response.StatusCode > 499
                ? LogEventLevel.Error
                : LogEventLevel.Information;
    });

    // 全局异常处理中间件
    app.Use(async (context, next) =>
    {
        try
        {
            await next();
        }
        catch (ValidationException ex)
        {
            Log.Warning(ex, "Validation error occurred: {Message}", ex.Message);
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(ex.Message));
        }
        catch (NotFoundException ex)
        {
            Log.Warning(ex, "Resource not found: {Message}", ex.Message);
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception occurred");
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail("服务器内部错误，请稍后重试"));
        }
    });

    // Swagger 仅在开发环境启用
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    // ============================================================================
    // 数据库初始化
    // ============================================================================
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ScraperDbContext>();
        db.Database.EnsureCreated();
        Log.Information("Database initialized successfully");
    }

    // ============================================================================
    // API 路由定义
    // ============================================================================

    // 健康检查
    app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
       .WithName("HealthCheck")
       .WithTags("System");

    // --------------------------------
    // 配置管理 API
    // --------------------------------
    var configGroup = app.MapGroup("/api/configs").WithTags("Configs");

    // 获取所有配置
    configGroup.MapGet("/", async (ScraperDbContext db, [FromQuery] bool? activeOnly) =>
    {
        Log.Information("Fetching all scrape configs, activeOnly: {ActiveOnly}", activeOnly);
        
        var query = db.ScrapeConfigs.AsQueryable();
        if (activeOnly == true)
        {
            query = query.Where(c => c.IsActive);
        }
        
        var configs = await query.OrderByDescending(c => c.CreatedAt).ToListAsync();
        return Results.Ok(ApiResponse<List<ScrapeConfig>>.Ok(configs));
    }).WithName("GetAllConfigs");

    // 获取单个配置
    configGroup.MapGet("/{id:int}", async (int id, ScraperDbContext db) =>
    {
        Log.Information("Fetching config with id: {Id}", id);
        
        var config = await db.ScrapeConfigs.FindAsync(id);
        if (config == null)
        {
            throw new NotFoundException($"配置 ID {id} 不存在");
        }
        
        return Results.Ok(ApiResponse<ScrapeConfig>.Ok(config));
    }).WithName("GetConfigById");

    // 创建配置
    configGroup.MapPost("/", async (CreateConfigRequest request, ScraperDbContext db) =>
    {
        // 参数验证
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ValidationException("配置名称不能为空");
        if (string.IsNullOrWhiteSpace(request.TargetUrl))
            throw new ValidationException("目标URL不能为空");
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out _))
            throw new ValidationException("目标URL格式不正确");
        if (string.IsNullOrWhiteSpace(request.XPathRules))
            throw new ValidationException("XPath规则不能为空");
        
        // 验证 XPath 规则 JSON 格式
        try
        {
            JsonSerializer.Deserialize<XPathRuleSet>(request.XPathRules);
        }
        catch
        {
            throw new ValidationException("XPath规则JSON格式不正确");
        }
        
        var config = new ScrapeConfig
        {
            Name = request.Name.Trim(),
            TargetUrl = request.TargetUrl.Trim(),
            XPathRules = request.XPathRules,
            IsActive = request.IsActive ?? true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        
        db.ScrapeConfigs.Add(config);
        await db.SaveChangesAsync();
        
        Log.Information("Created new config: {Name} (ID: {Id})", config.Name, config.Id);
        
        return Results.Created($"/api/configs/{config.Id}", ApiResponse<ScrapeConfig>.Ok(config, "配置创建成功"));
    }).WithName("CreateConfig");

    // 更新配置
    configGroup.MapPut("/{id:int}", async (int id, UpdateConfigRequest request, ScraperDbContext db) =>
    {
        var config = await db.ScrapeConfigs.FindAsync(id);
        if (config == null)
        {
            throw new NotFoundException($"配置 ID {id} 不存在");
        }
        
        if (!string.IsNullOrWhiteSpace(request.Name))
            config.Name = request.Name.Trim();
        
        if (!string.IsNullOrWhiteSpace(request.TargetUrl))
        {
            if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out _))
                throw new ValidationException("目标URL格式不正确");
            config.TargetUrl = request.TargetUrl.Trim();
        }
        
        if (!string.IsNullOrWhiteSpace(request.XPathRules))
        {
            try
            {
                JsonSerializer.Deserialize<XPathRuleSet>(request.XPathRules);
            }
            catch
            {
                throw new ValidationException("XPath规则JSON格式不正确");
            }
            config.XPathRules = request.XPathRules;
        }
        
        if (request.IsActive.HasValue)
            config.IsActive = request.IsActive.Value;
        
        config.UpdatedAt = DateTime.UtcNow;
        
        await db.SaveChangesAsync();
        
        Log.Information("Updated config: {Name} (ID: {Id})", config.Name, config.Id);
        
        return Results.Ok(ApiResponse<ScrapeConfig>.Ok(config, "配置更新成功"));
    }).WithName("UpdateConfig");

    // 删除配置
    configGroup.MapDelete("/{id:int}", async (int id, ScraperDbContext db) =>
    {
        var config = await db.ScrapeConfigs.FindAsync(id);
        if (config == null)
        {
            throw new NotFoundException($"配置 ID {id} 不存在");
        }
        
        // 同时删除相关数据和日志
        var relatedData = await db.ScrapedData.Where(d => d.ConfigId == id).ToListAsync();
        var relatedLogs = await db.ScrapeLogs.Where(l => l.ConfigId == id).ToListAsync();
        
        db.ScrapedData.RemoveRange(relatedData);
        db.ScrapeLogs.RemoveRange(relatedLogs);
        db.ScrapeConfigs.Remove(config);
        
        await db.SaveChangesAsync();
        
        Log.Information("Deleted config: {Name} (ID: {Id}) with {DataCount} data records and {LogCount} logs", 
            config.Name, config.Id, relatedData.Count, relatedLogs.Count);
        
        return Results.Ok(ApiResponse<object>.Ok(null, "配置删除成功"));
    }).WithName("DeleteConfig");

    // --------------------------------
    // 抓取任务 API
    // --------------------------------
    var scrapeGroup = app.MapGroup("/api/scrape").WithTags("Scrape");

    // 执行单个配置的抓取
    scrapeGroup.MapPost("/{configId:int}", async (int configId, IScraperService scraperService, ScraperDbContext db) =>
    {
        var config = await db.ScrapeConfigs.FindAsync(configId);
        if (config == null)
        {
            throw new NotFoundException($"配置 ID {configId} 不存在");
        }
        
        Log.Information("Starting scrape task for config: {Name} (ID: {Id})", config.Name, config.Id);
        
        var result = await scraperService.ScrapeAsync(config);
        
        return Results.Ok(ApiResponse<ScrapeResult>.Ok(result, 
            result.Success ? "抓取完成" : "抓取失败"));
    }).WithName("ScrapeByConfigId");

    // 执行所有启用配置的抓取
    scrapeGroup.MapPost("/all", async (IScraperService scraperService, ScraperDbContext db) =>
    {
        var activeConfigs = await db.ScrapeConfigs.Where(c => c.IsActive).ToListAsync();
        
        if (!activeConfigs.Any())
        {
            return Results.Ok(ApiResponse<List<ScrapeResult>>.Ok(new List<ScrapeResult>(), "没有启用的配置"));
        }
        
        Log.Information("Starting scrape task for {Count} active configs", activeConfigs.Count);
        
        var results = new List<ScrapeResult>();
        foreach (var config in activeConfigs)
        {
            var result = await scraperService.ScrapeAsync(config);
            results.Add(result);
        }
        
        var successCount = results.Count(r => r.Success);
        return Results.Ok(ApiResponse<List<ScrapeResult>>.Ok(results, 
            $"抓取完成: {successCount}/{results.Count} 成功"));
    }).WithName("ScrapeAll");

    // --------------------------------
    // 数据查询 API
    // --------------------------------
    var dataGroup = app.MapGroup("/api/data").WithTags("Data");

    // 获取所有数据（分页）
    dataGroup.MapGet("/", async (
        ScraperDbContext db,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? configId = null) =>
    {
        Log.Information("Fetching scraped data, page: {Page}, pageSize: {PageSize}, configId: {ConfigId}", 
            page, pageSize, configId);
        
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;
        
        var query = db.ScrapedData.Include(d => d.Config).AsQueryable();
        
        if (configId.HasValue)
        {
            query = query.Where(d => d.ConfigId == configId.Value);
        }
        
        var totalCount = await query.CountAsync();
        var data = await query
            .OrderByDescending(d => d.ScrapedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new ScrapedDataDto
            {
                Id = d.Id,
                ConfigId = d.ConfigId,
                ConfigName = d.Config!.Name,
                ExtractedData = d.ExtractedData,
                ScrapedAt = d.ScrapedAt
            })
            .ToListAsync();
        
        var result = new PagedResult<ScrapedDataDto>
        {
            Items = data,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        };
        
        return Results.Ok(ApiResponse<PagedResult<ScrapedDataDto>>.Ok(result));
    }).WithName("GetAllData");

    // 获取指定配置的数据
    dataGroup.MapGet("/{configId:int}", async (int configId, ScraperDbContext db, [FromQuery] int limit = 50) =>
    {
        var config = await db.ScrapeConfigs.FindAsync(configId);
        if (config == null)
        {
            throw new NotFoundException($"配置 ID {configId} 不存在");
        }
        
        if (limit < 1) limit = 50;
        if (limit > 500) limit = 500;
        
        var data = await db.ScrapedData
            .Where(d => d.ConfigId == configId)
            .OrderByDescending(d => d.ScrapedAt)
            .Take(limit)
            .Select(d => new ScrapedDataDto
            {
                Id = d.Id,
                ConfigId = d.ConfigId,
                ConfigName = config.Name,
                ExtractedData = d.ExtractedData,
                ScrapedAt = d.ScrapedAt
            })
            .ToListAsync();
        
        return Results.Ok(ApiResponse<List<ScrapedDataDto>>.Ok(data));
    }).WithName("GetDataByConfigId");

    // 删除数据
    dataGroup.MapDelete("/{id:int}", async (int id, ScraperDbContext db) =>
    {
        var data = await db.ScrapedData.FindAsync(id);
        if (data == null)
        {
            throw new NotFoundException($"数据 ID {id} 不存在");
        }
        
        db.ScrapedData.Remove(data);
        await db.SaveChangesAsync();
        
        Log.Information("Deleted scraped data ID: {Id}", id);
        
        return Results.Ok(ApiResponse<object>.Ok(null, "数据删除成功"));
    }).WithName("DeleteData");

    // --------------------------------
    // 日志查询 API
    // --------------------------------
    var logGroup = app.MapGroup("/api/logs").WithTags("Logs");

    // 获取抓取日志
    logGroup.MapGet("/", async (
        ScraperDbContext db,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? configId = null) =>
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;
        
        var query = db.ScrapeLogs.Include(l => l.Config).AsQueryable();
        
        if (configId.HasValue)
        {
            query = query.Where(l => l.ConfigId == configId.Value);
        }
        
        var totalCount = await query.CountAsync();
        var logs = await query
            .OrderByDescending(l => l.ExecutedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new ScrapeLogDto
            {
                Id = l.Id,
                ConfigId = l.ConfigId,
                ConfigName = l.Config!.Name,
                Status = l.Status,
                NewRecords = l.NewRecords,
                DuplicateRecords = l.DuplicateRecords,
                ErrorMessage = l.ErrorMessage,
                ExecutedAt = l.ExecutedAt
            })
            .ToListAsync();
        
        var result = new PagedResult<ScrapeLogDto>
        {
            Items = logs,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        };
        
        return Results.Ok(ApiResponse<PagedResult<ScrapeLogDto>>.Ok(result));
    }).WithName("GetLogs");

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.Information("Application shutting down...");
    Log.CloseAndFlush();
}

// ============================================================================
// 数据库上下文
// ============================================================================
public class ScraperDbContext : DbContext
{
    public ScraperDbContext(DbContextOptions<ScraperDbContext> options) : base(options) { }
    
    public DbSet<ScrapeConfig> ScrapeConfigs => Set<ScrapeConfig>();
    public DbSet<ScrapedData> ScrapedData => Set<ScrapedData>();
    public DbSet<ScrapeLog> ScrapeLogs => Set<ScrapeLog>();
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ScrapeConfig 配置
        modelBuilder.Entity<ScrapeConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TargetUrl).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.XPathRules).IsRequired();
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.IsActive);
        });
        
        // ScrapedData 配置
        modelBuilder.Entity<ScrapedData>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContentHash).IsRequired().HasMaxLength(64);
            // 使用组合索引加速去重查询
            entity.HasIndex(e => new { e.ContentHash, e.ConfigId }).IsUnique();
            entity.HasIndex(e => e.ConfigId);
            entity.HasIndex(e => e.ScrapedAt);
            
            entity.HasOne(e => e.Config)
                  .WithMany(c => c.ScrapedDataList)
                  .HasForeignKey(e => e.ConfigId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
        
        // ScrapeLog 配置
        modelBuilder.Entity<ScrapeLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.ConfigId);
            entity.HasIndex(e => e.ExecutedAt);
            
            entity.HasOne(e => e.Config)
                  .WithMany(c => c.ScrapeLogs)
                  .HasForeignKey(e => e.ConfigId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

// ============================================================================
// 实体模型
// ============================================================================

/// <summary>
/// 抓取配置实体
/// </summary>
public class ScrapeConfig
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(2000)]
    public string TargetUrl { get; set; } = string.Empty;
    
    [Required]
    public string XPathRules { get; set; } = string.Empty;
    
    public bool IsActive { get; set; } = true;
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime UpdatedAt { get; set; }
    
    // 导航属性
    [JsonIgnore]
    public List<ScrapedData> ScrapedDataList { get; set; } = new();
    
    [JsonIgnore]
    public List<ScrapeLog> ScrapeLogs { get; set; } = new();
}

/// <summary>
/// 抓取数据实体
/// </summary>
public class ScrapedData
{
    public int Id { get; set; }
    
    public int ConfigId { get; set; }
    
    [Required]
    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;
    
    public string? RawData { get; set; }
    
    public string ExtractedData { get; set; } = string.Empty;
    
    public DateTime ScrapedAt { get; set; }
    
    // 导航属性
    [JsonIgnore]
    public ScrapeConfig? Config { get; set; }
}

/// <summary>
/// 抓取日志实体
/// </summary>
public class ScrapeLog
{
    public int Id { get; set; }
    
    public int ConfigId { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = string.Empty;
    
    public int NewRecords { get; set; }
    
    public int DuplicateRecords { get; set; }
    
    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }
    
    public DateTime ExecutedAt { get; set; }
    
    // 导航属性
    [JsonIgnore]
    public ScrapeConfig? Config { get; set; }
}

// ============================================================================
// XPath 规则模型
// ============================================================================

/// <summary>
/// XPath 规则集
/// </summary>
public class XPathRuleSet
{
    [JsonPropertyName("rules")]
    public List<XPathRule> Rules { get; set; } = new();
}

/// <summary>
/// XPath 规则
/// </summary>
public class XPathRule
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("xpath")]
    public string XPath { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    public string Type { get; set; } = "single"; // single 或 list
    
    [JsonPropertyName("children")]
    public List<XPathRule>? Children { get; set; }
}

// ============================================================================
// DTO 模型
// ============================================================================

/// <summary>
/// 创建配置请求
/// </summary>
public record CreateConfigRequest(
    string Name,
    string TargetUrl,
    string XPathRules,
    bool? IsActive
);

/// <summary>
/// 更新配置请求
/// </summary>
public record UpdateConfigRequest(
    string? Name,
    string? TargetUrl,
    string? XPathRules,
    bool? IsActive
);

/// <summary>
/// 抓取数据 DTO
/// </summary>
public class ScrapedDataDto
{
    public int Id { get; set; }
    public int ConfigId { get; set; }
    public string ConfigName { get; set; } = string.Empty;
    public string ExtractedData { get; set; } = string.Empty;
    public DateTime ScrapedAt { get; set; }
}

/// <summary>
/// 抓取日志 DTO
/// </summary>
public class ScrapeLogDto
{
    public int Id { get; set; }
    public int ConfigId { get; set; }
    public string ConfigName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int NewRecords { get; set; }
    public int DuplicateRecords { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ExecutedAt { get; set; }
}

/// <summary>
/// 抓取结果
/// </summary>
public class ScrapeResult
{
    public int ConfigId { get; set; }
    public string ConfigName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int NewRecords { get; set; }
    public int DuplicateRecords { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ExecutedAt { get; set; }
}

/// <summary>
/// 分页结果
/// </summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// 统一 API 响应
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public static ApiResponse<T> Ok(T? data, string? message = null) => new()
    {
        Success = true,
        Message = message,
        Data = data
    };
    
    public static ApiResponse<T> Fail(string message) => new()
    {
        Success = false,
        Message = message,
        Data = default
    };
}

// ============================================================================
// 自定义异常
// ============================================================================

/// <summary>
/// 资源未找到异常
/// </summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

/// <summary>
/// 验证异常（避免与 System.ComponentModel.DataAnnotations.ValidationException 冲突）
/// </summary>
public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}

// ============================================================================
// 抓取服务
// ============================================================================

/// <summary>
/// 抓取服务接口
/// </summary>
public interface IScraperService
{
    Task<ScrapeResult> ScrapeAsync(ScrapeConfig config);
}

/// <summary>
/// 抓取服务实现
/// </summary>
public class ScraperService : IScraperService
{
    private readonly HttpClient _httpClient;
    private readonly ScraperDbContext _dbContext;
    private readonly ILogger<ScraperService> _logger;
    
    public ScraperService(HttpClient httpClient, ScraperDbContext dbContext, ILogger<ScraperService> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _logger = logger;
    }
    
    public async Task<ScrapeResult> ScrapeAsync(ScrapeConfig config)
    {
        var result = new ScrapeResult
        {
            ConfigId = config.Id,
            ConfigName = config.Name,
            ExecutedAt = DateTime.UtcNow
        };
        
        try
        {
            _logger.LogInformation("Starting scrape for config: {ConfigName} (ID: {ConfigId}), URL: {Url}", 
                config.Name, config.Id, config.TargetUrl);
            
            // 1. 获取网页内容
            var html = await FetchHtmlAsync(config.TargetUrl);
            _logger.LogDebug("Fetched HTML content, length: {Length} bytes", html.Length);
            
            // 2. 解析 XPath 规则
            var ruleSet = JsonSerializer.Deserialize<XPathRuleSet>(config.XPathRules);
            if (ruleSet?.Rules == null || !ruleSet.Rules.Any())
            {
                throw new Exception("XPath 规则为空或格式不正确");
            }
            
            _logger.LogDebug("Parsed {RuleCount} XPath rules", ruleSet.Rules.Count);
            
            // 3. 提取数据
            var extractedItems = ExtractData(html, ruleSet);
            
            _logger.LogInformation("Extracted {Count} items from {Url}", 
                extractedItems.Count, config.TargetUrl);
            
            // 4. 去重并保存
            int newCount = 0;
            int duplicateCount = 0;
            
            foreach (var item in extractedItems)
            {
                // 使用稳定排序的哈希计算，避免 Dictionary 顺序不稳定导致的去重问题
                var (hash, jsonData) = ComputeStableHash(item);
                
                // 检查是否已存在（使用组合索引优化查询）
                var exists = await _dbContext.ScrapedData
                    .AnyAsync(d => d.ContentHash == hash && d.ConfigId == config.Id);
                
                if (exists)
                {
                    duplicateCount++;
                    _logger.LogDebug("Duplicate data detected, hash: {Hash}", hash[..16]);
                    continue;
                }
                
                // 保存新数据
                var scrapedData = new ScrapedData
                {
                    ConfigId = config.Id,
                    ContentHash = hash,
                    RawData = html.Length > 10000 ? null : html, // 原始HTML太大则不保存
                    ExtractedData = jsonData,
                    ScrapedAt = DateTime.UtcNow
                };
                
                _dbContext.ScrapedData.Add(scrapedData);
                newCount++;
                
                _logger.LogDebug("New data added, hash: {Hash}", hash[..16]);
            }
            
            if (newCount > 0)
            {
                await _dbContext.SaveChangesAsync();
            }
            
            result.Success = true;
            result.NewRecords = newCount;
            result.DuplicateRecords = duplicateCount;
            
            _logger.LogInformation("Scrape completed for {ConfigName}: {New} new, {Dup} duplicates", 
                config.Name, newCount, duplicateCount);
        }
        catch (HttpRequestException ex)
        {
            result.Success = false;
            result.ErrorMessage = $"网络请求失败: {ex.Message}";
            _logger.LogError(ex, "HTTP request failed for config: {ConfigName}, URL: {Url}", 
                config.Name, config.TargetUrl);
        }
        catch (JsonException ex)
        {
            result.Success = false;
            result.ErrorMessage = $"JSON 解析失败: {ex.Message}";
            _logger.LogError(ex, "JSON parsing failed for config: {ConfigName}", config.Name);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Scrape failed for config: {ConfigName}", config.Name);
        }
        
        // 记录日志（独立的 try-catch 确保日志一定能被记录）
        try
        {
            var log = new ScrapeLog
            {
                ConfigId = config.Id,
                Status = result.Success ? "Success" : "Failed",
                NewRecords = result.NewRecords,
                DuplicateRecords = result.DuplicateRecords,
                ErrorMessage = result.ErrorMessage?.Length > 2000 
                    ? result.ErrorMessage[..2000] 
                    : result.ErrorMessage,
                ExecutedAt = result.ExecutedAt
            };
            
            _dbContext.ScrapeLogs.Add(log);
            await _dbContext.SaveChangesAsync();
            
            _logger.LogDebug("Scrape log recorded for config: {ConfigName}, status: {Status}", 
                config.Name, log.Status);
        }
        catch (Exception ex)
        {
            // 日志保存失败不影响主流程，但需要记录错误
            _logger.LogError(ex, "Failed to save scrape log for config: {ConfigName}", config.Name);
        }
        
        return result;
    }
    
    /// <summary>
    /// 获取网页 HTML 内容
    /// </summary>
    private async Task<string> FetchHtmlAsync(string url)
    {
        _logger.LogDebug("Fetching HTML from URL: {Url}", url);
        
        var response = await _httpClient.GetAsync(url);
        
        _logger.LogDebug("HTTP response status: {StatusCode}", response.StatusCode);
        
        response.EnsureSuccessStatusCode();
        
        var content = await response.Content.ReadAsStringAsync();
        
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new Exception("获取到的网页内容为空");
        }
        
        return content;
    }
    
    /// <summary>
    /// 根据 XPath 规则提取数据
    /// </summary>
    private List<Dictionary<string, object>> ExtractData(string html, XPathRuleSet ruleSet)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        
        var results = new List<Dictionary<string, object>>();
        
        // 检查是否有 list 类型的规则
        var listRule = ruleSet.Rules.FirstOrDefault(r => r.Type == "list");
        
        if (listRule != null)
        {
            _logger.LogDebug("Using list mode extraction with XPath: {XPath}", listRule.XPath);
            
            // 列表模式：提取多个项目
            var nodes = doc.DocumentNode.SelectNodes(listRule.XPath);
            if (nodes != null)
            {
                _logger.LogDebug("Found {NodeCount} matching nodes", nodes.Count);
                
                foreach (var node in nodes)
                {
                    var item = new Dictionary<string, object>();
                    
                    // 提取子规则
                    if (listRule.Children != null)
                    {
                        foreach (var child in listRule.Children)
                        {
                            var value = ExtractSingleValue(node, child.XPath);
                            if (value != null)
                            {
                                item[child.Name] = value;
                            }
                        }
                    }
                    
                    // 提取其他单值规则
                    foreach (var rule in ruleSet.Rules.Where(r => r.Type != "list"))
                    {
                        var value = ExtractSingleValue(doc.DocumentNode, rule.XPath);
                        if (value != null)
                        {
                            item[rule.Name] = value;
                        }
                    }
                    
                    if (item.Any())
                    {
                        results.Add(item);
                    }
                }
            }
            else
            {
                _logger.LogWarning("No nodes found for XPath: {XPath}", listRule.XPath);
            }
        }
        else
        {
            _logger.LogDebug("Using single mode extraction");
            
            // 单项模式：只提取一条数据
            var item = new Dictionary<string, object>();
            
            foreach (var rule in ruleSet.Rules)
            {
                var value = ExtractSingleValue(doc.DocumentNode, rule.XPath);
                if (value != null)
                {
                    item[rule.Name] = value;
                    _logger.LogDebug("Extracted '{Name}': {Value}", rule.Name, 
                        value.Length > 50 ? value[..50] + "..." : value);
                }
                else
                {
                    _logger.LogDebug("No value found for rule '{Name}' with XPath: {XPath}", 
                        rule.Name, rule.XPath);
                }
            }
            
            if (item.Any())
            {
                results.Add(item);
            }
        }
        
        return results;
    }
    
    /// <summary>
    /// 提取单个值
    /// 支持标准 XPath 语法，包括：
    /// - 元素选择：//div, //a[@class='link']
    /// - 属性选择：//a/@href, //img/@src
    /// - 文本选择：//p/text()
    /// - 复杂表达式：//a[contains(@class, 'link')]/@href
    /// </summary>
    private string? ExtractSingleValue(HtmlNode contextNode, string xpath)
    {
        try
        {
            // 使用 HtmlAgilityPack 原生 XPath 导航器处理所有 XPath 表达式
            var navigator = contextNode.CreateNavigator();
            var result = navigator.Evaluate(xpath);
            
            // 处理不同类型的 XPath 结果
            if (result is HtmlAgilityPack.HtmlNodeNavigator nodeNav)
            {
                // 单节点结果
                return ExtractValueFromNavigator(nodeNav);
            }
            else if (result is System.Xml.XPath.XPathNodeIterator iterator)
            {
                // 节点集合，取第一个
                if (iterator.MoveNext() && iterator.Current != null)
                {
                    return ExtractValueFromNavigator(iterator.Current);
                }
            }
            else if (result is string strResult)
            {
                // 字符串结果（如 XPath 函数返回值）
                return strResult.Trim();
            }
            else if (result != null)
            {
                // 其他类型（数字、布尔等）
                return result.ToString()?.Trim();
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract value with XPath: {XPath}", xpath);
            return null;
        }
    }
    
    /// <summary>
    /// 从 XPath 导航器提取值
    /// </summary>
    private static string? ExtractValueFromNavigator(System.Xml.XPath.XPathNavigator? navigator)
    {
        if (navigator == null) return null;
        
        // 根据节点类型提取值
        return navigator.NodeType switch
        {
            System.Xml.XPath.XPathNodeType.Attribute => navigator.Value?.Trim(),
            System.Xml.XPath.XPathNodeType.Text => navigator.Value?.Trim(),
            System.Xml.XPath.XPathNodeType.Element => HtmlEntity.DeEntitize(navigator.Value)?.Trim(),
            _ => navigator.Value?.Trim()
        };
    }
    
    /// <summary>
    /// 计算稳定的内容哈希（用于去重）
    /// 对 Dictionary 的键进行排序后再序列化，确保相同内容始终产生相同哈希
    /// </summary>
    private static (string Hash, string JsonData) ComputeStableHash(Dictionary<string, object> data)
    {
        // 递归排序所有键，确保序列化结果稳定
        var sortedData = SortDictionaryKeys(data);
        
        var jsonData = JsonSerializer.Serialize(sortedData, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
        
        var bytes = Encoding.UTF8.GetBytes(jsonData);
        var hashBytes = SHA256.HashData(bytes);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        
        return (hash, jsonData);
    }
    
    /// <summary>
    /// 递归排序 Dictionary 的键
    /// </summary>
    private static SortedDictionary<string, object> SortDictionaryKeys(Dictionary<string, object> data)
    {
        var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
        
        foreach (var kvp in data)
        {
            if (kvp.Value is Dictionary<string, object> nestedDict)
            {
                sorted[kvp.Key] = SortDictionaryKeys(nestedDict);
            }
            else if (kvp.Value is List<Dictionary<string, object>> listOfDicts)
            {
                sorted[kvp.Key] = listOfDicts.Select(SortDictionaryKeys).ToList();
            }
            else
            {
                sorted[kvp.Key] = kvp.Value;
            }
        }
        
        return sorted;
    }
}

// 使 Program 类对测试项目可见
public partial class Program { }

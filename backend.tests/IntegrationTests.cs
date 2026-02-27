// ============================================================================
// Web Scraper 集成测试 - API 端点测试
// ============================================================================

using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace WebScraper.Tests;

/// <summary>
/// API 集成测试
/// </summary>
public class IntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public IntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // 移除原有的 DbContext 注册
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<ScraperDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                // 使用内存数据库
                services.AddDbContext<ScraperDbContext>(options =>
                {
                    options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid());
                });
            });
        });
        
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    #region Health Check 测试

    [Fact]
    public async Task HealthCheck_ShouldReturnOk()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("status").GetString().Should().Be("healthy");
    }

    #endregion

    #region 配置管理 API 测试

    [Fact]
    public async Task CreateConfig_WithValidData_ShouldReturnCreated()
    {
        // Arrange
        var request = new
        {
            name = "Test Config",
            targetUrl = "https://example.com",
            xPathRules = "{\"rules\":[{\"name\":\"title\",\"xpath\":\"//h1\",\"type\":\"single\"}]}",
            isActive = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/configs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("success").GetBoolean().Should().BeTrue();
        content.GetProperty("data").GetProperty("name").GetString().Should().Be("Test Config");
    }

    [Fact]
    public async Task CreateConfig_WithEmptyName_ShouldReturnBadRequest()
    {
        // Arrange
        var request = new
        {
            name = "",
            targetUrl = "https://example.com",
            xPathRules = "{\"rules\":[]}",
            isActive = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/configs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateConfig_WithInvalidUrl_ShouldReturnBadRequest()
    {
        // Arrange
        var request = new
        {
            name = "Test",
            targetUrl = "not-a-valid-url",
            xPathRules = "{\"rules\":[]}",
            isActive = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/configs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateConfig_WithInvalidXPathJson_ShouldReturnBadRequest()
    {
        // Arrange
        var request = new
        {
            name = "Test",
            targetUrl = "https://example.com",
            xPathRules = "invalid json",
            isActive = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/configs", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetConfigs_ShouldReturnEmptyListInitially()
    {
        // Act
        var response = await _client.GetAsync("/api/configs");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetConfigById_WithNonExistentId_ShouldReturnNotFound()
    {
        // Act
        var response = await _client.GetAsync("/api/configs/99999");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateConfig_WithValidData_ShouldReturnOk()
    {
        // Arrange - 先创建一个配置
        var createRequest = new
        {
            name = "Original Name",
            targetUrl = "https://example.com",
            xPathRules = "{\"rules\":[]}",
            isActive = true
        };
        var createResponse = await _client.PostAsJsonAsync("/api/configs", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var configId = created.GetProperty("data").GetProperty("id").GetInt32();

        // Act - 更新配置
        var updateRequest = new
        {
            name = "Updated Name"
        };
        var response = await _client.PutAsJsonAsync($"/api/configs/{configId}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("data").GetProperty("name").GetString().Should().Be("Updated Name");
    }

    [Fact]
    public async Task DeleteConfig_WithValidId_ShouldReturnOk()
    {
        // Arrange - 先创建一个配置
        var createRequest = new
        {
            name = "To Delete",
            targetUrl = "https://example.com",
            xPathRules = "{\"rules\":[]}",
            isActive = true
        };
        var createResponse = await _client.PostAsJsonAsync("/api/configs", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var configId = created.GetProperty("data").GetProperty("id").GetInt32();

        // Act
        var response = await _client.DeleteAsync($"/api/configs/{configId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify deletion
        var getResponse = await _client.GetAsync($"/api/configs/{configId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region 数据查询 API 测试

    [Fact]
    public async Task GetData_WithPagination_ShouldReturnPagedResult()
    {
        // Act
        var response = await _client.GetAsync("/api/data?page=1&pageSize=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("data").GetProperty("page").GetInt32().Should().Be(1);
        content.GetProperty("data").GetProperty("pageSize").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task GetData_WithInvalidPage_ShouldNormalizePage()
    {
        // Act
        var response = await _client.GetAsync("/api/data?page=-1&pageSize=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("data").GetProperty("page").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetData_WithExcessivePageSize_ShouldLimitPageSize()
    {
        // Act
        var response = await _client.GetAsync("/api/data?page=1&pageSize=500");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("data").GetProperty("pageSize").GetInt32().Should().BeLessOrEqualTo(100);
    }

    [Fact]
    public async Task GetDataByConfigId_WithNonExistentId_ShouldReturnNotFound()
    {
        // Act
        var response = await _client.GetAsync("/api/data/99999");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region 日志查询 API 测试

    [Fact]
    public async Task GetLogs_ShouldReturnPagedResult()
    {
        // Act
        var response = await _client.GetAsync("/api/logs");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    #endregion

    #region 抓取 API 测试

    [Fact]
    public async Task Scrape_WithNonExistentConfig_ShouldReturnNotFound()
    {
        // Act
        var response = await _client.PostAsync("/api/scrape/99999", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ScrapeAll_WithNoActiveConfigs_ShouldReturnEmptyResult()
    {
        // Act
        var response = await _client.PostAsync("/api/scrape/all", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("message").GetString().Should().Contain("没有启用的配置");
    }

    #endregion
}

/// <summary>
/// 并发测试
/// </summary>
public class ConcurrencyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ConcurrencyTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<ScraperDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<ScraperDbContext>(options =>
                {
                    options.UseInMemoryDatabase("ConcurrencyTestDb_" + Guid.NewGuid());
                });
            });
        });
    }

    [Fact]
    public async Task ConcurrentRequests_ShouldHandleGracefully()
    {
        // Arrange
        var client = _factory.CreateClient();
        var tasks = new List<Task<HttpResponseMessage>>();

        // Act - 并发发送 10 个请求
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(client.GetAsync("/health"));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert
        foreach (var response in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}

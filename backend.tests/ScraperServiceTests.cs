// ============================================================================
// Web Scraper 单元测试 - 抓取服务测试
// ============================================================================

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;
using Xunit;

namespace WebScraper.Tests;

/// <summary>
/// ScraperService 单元测试
/// </summary>
public class ScraperServiceTests : IDisposable
{
    private readonly ScraperDbContext _dbContext;
    private readonly Mock<ILogger<ScraperService>> _loggerMock;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;

    public ScraperServiceTests()
    {
        // 使用内存数据库
        var options = new DbContextOptionsBuilder<ScraperDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        _dbContext = new ScraperDbContext(options);
        _loggerMock = new Mock<ILogger<ScraperService>>();
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _httpClient.Dispose();
    }

    #region 辅助方法

    private void SetupHttpResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content)
            });
    }

    private ScrapeConfig CreateTestConfig(string name = "Test Config", string url = "https://example.com")
    {
        return new ScrapeConfig
        {
            Id = 1,
            Name = name,
            TargetUrl = url,
            XPathRules = JsonSerializer.Serialize(new XPathRuleSet
            {
                Rules = new List<XPathRule>
                {
                    new() { Name = "title", XPath = "//h1/text()", Type = "single" }
                }
            }),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    #endregion

    #region ScrapeAsync 测试

    [Fact]
    public async Task ScrapeAsync_WithValidHtml_ShouldExtractDataSuccessfully()
    {
        // Arrange
        var html = "<html><body><h1>Test Title</h1></body></html>";
        SetupHttpResponse(html);
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();

        // Act
        var result = await service.ScrapeAsync(config);

        // Assert
        result.Success.Should().BeTrue();
        result.NewRecords.Should().Be(1);
        result.DuplicateRecords.Should().Be(0);
        result.ConfigId.Should().Be(config.Id);
        result.ConfigName.Should().Be(config.Name);
    }

    [Fact]
    public async Task ScrapeAsync_WithDuplicateData_ShouldNotSaveDuplicates()
    {
        // Arrange
        var html = "<html><body><h1>Test Title</h1></body></html>";
        SetupHttpResponse(html);
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();

        // Act - 第一次抓取
        var result1 = await service.ScrapeAsync(config);
        
        // Act - 第二次抓取（相同数据）
        var result2 = await service.ScrapeAsync(config);

        // Assert
        result1.Success.Should().BeTrue();
        result1.NewRecords.Should().Be(1);
        
        result2.Success.Should().BeTrue();
        result2.NewRecords.Should().Be(0);
        result2.DuplicateRecords.Should().Be(1);
        
        // 数据库中应该只有一条数据
        var dataCount = await _dbContext.ScrapedData.CountAsync();
        dataCount.Should().Be(1);
    }

    [Fact]
    public async Task ScrapeAsync_WithHttpError_ShouldReturnFailure()
    {
        // Arrange
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.NotFound
            });
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();

        // Act
        var result = await service.ScrapeAsync(config);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ScrapeAsync_WithEmptyHtml_ShouldReturnFailure()
    {
        // Arrange
        SetupHttpResponse("");
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();

        // Act
        var result = await service.ScrapeAsync(config);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("空");
    }

    [Fact]
    public async Task ScrapeAsync_WithInvalidXPathRules_ShouldReturnFailure()
    {
        // Arrange
        var html = "<html><body><h1>Test Title</h1></body></html>";
        SetupHttpResponse(html);
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();
        config.XPathRules = "invalid json";

        // Act
        var result = await service.ScrapeAsync(config);

        // Assert
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ScrapeAsync_WithEmptyXPathRules_ShouldReturnFailure()
    {
        // Arrange
        var html = "<html><body><h1>Test Title</h1></body></html>";
        SetupHttpResponse(html);
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();
        config.XPathRules = JsonSerializer.Serialize(new XPathRuleSet { Rules = new List<XPathRule>() });

        // Act
        var result = await service.ScrapeAsync(config);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("规则");
    }

    [Fact]
    public async Task ScrapeAsync_WithListTypeRule_ShouldExtractMultipleItems()
    {
        // Arrange
        var html = @"
            <html><body>
                <div class='item'><span class='name'>Item 1</span><span class='price'>$10</span></div>
                <div class='item'><span class='name'>Item 2</span><span class='price'>$20</span></div>
                <div class='item'><span class='name'>Item 3</span><span class='price'>$30</span></div>
            </body></html>";
        SetupHttpResponse(html);
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();
        config.XPathRules = JsonSerializer.Serialize(new XPathRuleSet
        {
            Rules = new List<XPathRule>
            {
                new()
                {
                    Name = "items",
                    XPath = "//div[@class='item']",
                    Type = "list",
                    Children = new List<XPathRule>
                    {
                        new() { Name = "name", XPath = ".//span[@class='name']/text()" },
                        new() { Name = "price", XPath = ".//span[@class='price']/text()" }
                    }
                }
            }
        });

        // Act
        var result = await service.ScrapeAsync(config);

        // Assert
        result.Success.Should().BeTrue();
        result.NewRecords.Should().Be(3);
    }

    [Fact]
    public async Task ScrapeAsync_ShouldRecordLog()
    {
        // Arrange
        var html = "<html><body><h1>Test Title</h1></body></html>";
        SetupHttpResponse(html);
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();

        // Act
        await service.ScrapeAsync(config);

        // Assert
        var log = await _dbContext.ScrapeLogs.FirstOrDefaultAsync();
        log.Should().NotBeNull();
        log!.ConfigId.Should().Be(config.Id);
        log.Status.Should().Be("Success");
    }

    [Fact]
    public async Task ScrapeAsync_WithNoMatchingXPath_ShouldReturnZeroRecords()
    {
        // Arrange
        var html = "<html><body><p>No h1 here</p></body></html>";
        SetupHttpResponse(html);
        
        var service = new ScraperService(_httpClient, _dbContext, _loggerMock.Object);
        var config = CreateTestConfig();

        // Act
        var result = await service.ScrapeAsync(config);

        // Assert
        result.Success.Should().BeTrue();
        result.NewRecords.Should().Be(0);
    }

    #endregion
}

/// <summary>
/// 数据模型测试
/// </summary>
public class ModelTests
{
    [Fact]
    public void ApiResponse_Ok_ShouldSetCorrectValues()
    {
        // Act
        var response = ApiResponse<string>.Ok("test data", "success message");

        // Assert
        response.Success.Should().BeTrue();
        response.Data.Should().Be("test data");
        response.Message.Should().Be("success message");
        response.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ApiResponse_Fail_ShouldSetCorrectValues()
    {
        // Act
        var response = ApiResponse<string>.Fail("error message");

        // Assert
        response.Success.Should().BeFalse();
        response.Data.Should().BeNull();
        response.Message.Should().Be("error message");
    }

    [Fact]
    public void XPathRuleSet_Deserialization_ShouldWork()
    {
        // Arrange
        var json = @"{""rules"":[{""name"":""title"",""xpath"":""//h1"",""type"":""single""}]}";

        // Act
        var ruleSet = JsonSerializer.Deserialize<XPathRuleSet>(json);

        // Assert
        ruleSet.Should().NotBeNull();
        ruleSet!.Rules.Should().HaveCount(1);
        ruleSet.Rules[0].Name.Should().Be("title");
        ruleSet.Rules[0].XPath.Should().Be("//h1");
        ruleSet.Rules[0].Type.Should().Be("single");
    }

    [Fact]
    public void PagedResult_ShouldCalculateTotalPagesCorrectly()
    {
        // Arrange & Act
        var result = new PagedResult<string>
        {
            Items = new List<string> { "a", "b", "c" },
            TotalCount = 25,
            Page = 1,
            PageSize = 10,
            TotalPages = (int)Math.Ceiling(25 / (double)10)
        };

        // Assert
        result.TotalPages.Should().Be(3);
    }
}

/// <summary>
/// 哈希计算测试
/// </summary>
public class HashTests
{
    [Fact]
    public void ComputeStableHash_SameContentDifferentOrder_ShouldReturnSameHash()
    {
        // Arrange - 创建两个内容相同但插入顺序不同的 Dictionary
        var dict1 = new Dictionary<string, object>
        {
            { "title", "Test" },
            { "author", "John" },
            { "year", "2024" }
        };
        
        var dict2 = new Dictionary<string, object>
        {
            { "year", "2024" },
            { "title", "Test" },
            { "author", "John" }
        };

        // Act
        var hash1 = ComputeStableTestHash(dict1);
        var hash2 = ComputeStableTestHash(dict2);

        // Assert - 排序后哈希应该相同
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeStableHash_DifferentContent_ShouldReturnDifferentHash()
    {
        // Arrange
        var dict1 = new Dictionary<string, object> { { "title", "Test1" } };
        var dict2 = new Dictionary<string, object> { { "title", "Test2" } };

        // Act
        var hash1 = ComputeStableTestHash(dict1);
        var hash2 = ComputeStableTestHash(dict2);

        // Assert
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeStableHash_ShouldReturn64CharacterHexString()
    {
        // Arrange
        var dict = new Dictionary<string, object> { { "key", "value" } };

        // Act
        var hash = ComputeStableTestHash(dict);

        // Assert
        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[a-f0-9]+$");
    }

    [Fact]
    public void ComputeStableHash_NestedDictionary_ShouldBeStable()
    {
        // Arrange - 嵌套 Dictionary 测试
        var dict1 = new Dictionary<string, object>
        {
            { "nested", new Dictionary<string, object>
                {
                    { "b", "2" },
                    { "a", "1" }
                }
            },
            { "name", "test" }
        };
        
        var dict2 = new Dictionary<string, object>
        {
            { "name", "test" },
            { "nested", new Dictionary<string, object>
                {
                    { "a", "1" },
                    { "b", "2" }
                }
            }
        };

        // Act
        var hash1 = ComputeStableTestHash(dict1);
        var hash2 = ComputeStableTestHash(dict2);

        // Assert - 嵌套结构排序后哈希也应该相同
        hash1.Should().Be(hash2);
    }

    /// <summary>
    /// 测试用的稳定哈希计算方法（模拟 ScraperService 中的实现）
    /// </summary>
    private static string ComputeStableTestHash(Dictionary<string, object> data)
    {
        var sortedData = SortDictionaryKeys(data);
        var jsonData = JsonSerializer.Serialize(sortedData, new JsonSerializerOptions 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });
        
        var bytes = System.Text.Encoding.UTF8.GetBytes(jsonData);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
    
    private static SortedDictionary<string, object> SortDictionaryKeys(Dictionary<string, object> data)
    {
        var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
        
        foreach (var kvp in data)
        {
            if (kvp.Value is Dictionary<string, object> nestedDict)
            {
                sorted[kvp.Key] = SortDictionaryKeys(nestedDict);
            }
            else
            {
                sorted[kvp.Key] = kvp.Value;
            }
        }
        
        return sorted;
    }
}

/// <summary>
/// 验证逻辑测试
/// </summary>
public class ValidationTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("https://example.com/path?query=1", true)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    [InlineData("ftp://example.com", true)]
    public void UrlValidation_ShouldWorkCorrectly(string url, bool expectedValid)
    {
        // Act
        var isValid = Uri.TryCreate(url, UriKind.Absolute, out _);

        // Assert
        isValid.Should().Be(expectedValid);
    }

    [Theory]
    [InlineData("{\"rules\":[]}", true)]
    [InlineData("{\"rules\":[{\"name\":\"test\",\"xpath\":\"//div\",\"type\":\"single\"}]}", true)]
    [InlineData("invalid json", false)]
    [InlineData("", false)]
    [InlineData("{}", true)]
    public void XPathRulesJsonValidation_ShouldWorkCorrectly(string json, bool expectedValid)
    {
        // Act
        bool isValid;
        try
        {
            JsonSerializer.Deserialize<XPathRuleSet>(json);
            isValid = true;
        }
        catch
        {
            isValid = false;
        }

        // Assert
        isValid.Should().Be(expectedValid);
    }
}

# Web Scraper 通用网页抓取框架

基于 .NET 9 + Mini API + EF Core + SQLite 构建的通用网页抓取框架，支持配置 URL 和 XPath 规则，自动去重保存数据。

## How to Run

### 方式一：Docker Compose（推荐）

```bash
# 构建并启动服务
docker-compose up --build -d

# 查看日志
docker-compose logs -f backend

# 停止服务
docker-compose down
```

### 方式二：本地开发运行

```bash
# 进入后端目录
cd backend

# 还原依赖
dotnet restore

# 运行项目
dotnet run
```

## Services

| 服务        | 端口         | 说明                   |
| ----------- | ------------ | ---------------------- |
| Backend API | 8082         | Web Scraper REST API   |
| Swagger UI  | 8082/swagger | API 文档（仅开发环境） |

**访问地址：**

- API 根地址：http://localhost:8082
- 健康检查：http://localhost:8082/health
- Swagger 文档：http://localhost:8082/swagger（开发环境）

## 测试账号

本项目为纯后端 API 服务，无需登录认证。

## 质检测试脚本

以下 curl 命令用于验证所有 API 功能，请按顺序执行：

### 0. 健康检查

```bash
# 检查服务是否正常运行
curl -s http://localhost:8082/health | jq .
```

**预期结果**：返回 `{"status": "healthy", ...}`

---

### 1. 配置管理测试

#### 1.1 创建配置 - 正常情况

```bash
curl -s -X POST http://localhost:8082/api/configs \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Example.com 测试",
    "targetUrl": "https://example.com",
    "xPathRules": "{\"rules\":[{\"name\":\"title\",\"xpath\":\"//h1/text()\",\"type\":\"single\"},{\"name\":\"description\",\"xpath\":\"//p/text()\",\"type\":\"single\"}]}",
    "isActive": true
  }' | jq .
```

**预期结果**：`success: true`，返回创建的配置信息

#### 1.2 创建配置 - 带属性选择器

```bash
curl -s -X POST http://localhost:8082/api/configs \
  -H "Content-Type: application/json" \
  -d '{
    "name": "链接提取测试",
    "targetUrl": "https://example.com",
    "xPathRules": "{\"rules\":[{\"name\":\"link\",\"xpath\":\"//a/@href\",\"type\":\"single\"},{\"name\":\"title\",\"xpath\":\"//h1/text()\",\"type\":\"single\"}]}",
    "isActive": true
  }' | jq .
```

**预期结果**：`success: true`

#### 1.3 创建配置 - 空名称（应失败）

```bash
curl -s -X POST http://localhost:8082/api/configs \
  -H "Content-Type: application/json" \
  -d '{
    "name": "",
    "targetUrl": "https://example.com",
    "xPathRules": "{}",
    "isActive": true
  }' | jq .
```

**预期结果**：`success: false`，`message: "配置名称不能为空"`

#### 1.4 创建配置 - 无效URL（应失败）

```bash
curl -s -X POST http://localhost:8082/api/configs \
  -H "Content-Type: application/json" \
  -d '{
    "name": "无效URL测试",
    "targetUrl": "not-a-valid-url",
    "xPathRules": "{}",
    "isActive": true
  }' | jq .
```

**预期结果**：`success: false`，`message: "目标URL格式不正确"`

#### 1.5 创建配置 - 无效JSON（应失败）

```bash
curl -s -X POST http://localhost:8082/api/configs \
  -H "Content-Type: application/json" \
  -d '{
    "name": "无效JSON测试",
    "targetUrl": "https://example.com",
    "xPathRules": "invalid json",
    "isActive": true
  }' | jq .
```

**预期结果**：`success: false`，`message: "XPath规则JSON格式不正确"`

#### 1.6 获取所有配置

```bash
curl -s http://localhost:8082/api/configs | jq .
```

**预期结果**：返回配置列表

#### 1.7 获取单个配置

```bash
curl -s http://localhost:8082/api/configs/1 | jq .
```

**预期结果**：返回 ID 为 1 的配置详情

#### 1.8 获取不存在的配置（应失败）

```bash
curl -s http://localhost:8082/api/configs/999 | jq .
```

**预期结果**：`success: false`，`message: "配置 ID 999 不存在"`

#### 1.9 更新配置

```bash
curl -s -X PUT http://localhost:8082/api/configs/1 \
  -H "Content-Type: application/json" \
  -d '{
    "name": "已更新的配置名称",
    "isActive": false
  }' | jq .
```

**预期结果**：`success: true`，名称和状态已更新

---

### 2. 抓取功能测试

#### 2.1 执行单个配置抓取

```bash
curl -s -X POST http://localhost:8082/api/scrape/1 | jq .
```

**预期结果**：`success: true`，显示 `newRecords` 和 `duplicateRecords` 数量

#### 2.2 执行属性选择器配置抓取（验证 Bug 修复）

```bash
curl -s -X POST http://localhost:8082/api/scrape/2 | jq .
```

**预期结果**：`success: true`，`newRecords >= 1`

#### 2.3 再次抓取验证去重（应为重复）

```bash
curl -s -X POST http://localhost:8082/api/scrape/2 | jq .
```

**预期结果**：`success: true`，`newRecords: 0`，`duplicateRecords: 1`

#### 2.4 抓取不存在的配置（应失败）

```bash
curl -s -X POST http://localhost:8082/api/scrape/999 | jq .
```

**预期结果**：`success: false`，`message: "配置 ID 999 不存在"`

#### 2.5 执行所有启用配置的抓取

```bash
curl -s -X POST http://localhost:8082/api/scrape/all | jq .
```

**预期结果**：返回所有启用配置的抓取结果数组

---

### 3. 数据查询测试

#### 3.1 获取所有数据（分页）

```bash
curl -s "http://localhost:8082/api/data?page=1&pageSize=10" | jq .
```

**预期结果**：返回分页数据，包含 `items`、`totalCount`、`page`、`pageSize`、`totalPages`

#### 3.2 获取指定配置的数据

```bash
curl -s http://localhost:8082/api/data/2 | jq .
```

**预期结果**：返回配置 ID 为 2 的抓取数据，包含 `link` 和 `title` 字段

#### 3.3 验证属性选择器提取结果

```bash
curl -s http://localhost:8082/api/data/2 | jq '.data[0].extractedData' | xargs echo -e
```

**预期结果**：JSON 中包含 `"link":"https://..."` 属性值

---

### 4. 日志查询测试

#### 4.1 获取所有日志

```bash
curl -s "http://localhost:8082/api/logs?page=1&pageSize=10" | jq .
```

**预期结果**：返回抓取日志列表

#### 4.2 获取指定配置的日志

```bash
curl -s "http://localhost:8082/api/logs?configId=2" | jq .
```

**预期结果**：返回配置 ID 为 2 的抓取日志

---

### 5. 删除功能测试

#### 5.1 删除数据

```bash
# 先查看数据 ID
curl -s http://localhost:8082/api/data | jq '.data.items[0].id'

# 删除指定数据（替换 {id} 为实际 ID）
curl -s -X DELETE http://localhost:8082/api/data/1 | jq .
```

**预期结果**：`success: true`，`message: "数据删除成功"`

#### 5.2 删除配置（级联删除数据和日志）

```bash
curl -s -X DELETE http://localhost:8082/api/configs/1 | jq .
```

**预期结果**：`success: true`，`message: "配置删除成功"`

---

### 6. 一键测试脚本

将以下脚本保存为 `test.sh` 并执行：

```bash
#!/bin/bash
# Web Scraper 质检测试脚本

BASE_URL="http://localhost:8082"
PASS=0
FAIL=0

check() {
    local name="$1"
    local expected="$2"
    local result="$3"

    if echo "$result" | grep -q "$expected"; then
        echo "✅ $name"
        ((PASS++))
    else
        echo "❌ $name"
        echo "   预期包含: $expected"
        echo "   实际结果: $result"
        ((FAIL++))
    fi
}

echo "=========================================="
echo "Web Scraper 质检测试"
echo "=========================================="
echo ""

# 健康检查
result=$(curl -s $BASE_URL/health)
check "健康检查" '"status":"healthy"' "$result"

# 创建配置
result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"测试配置","targetUrl":"https://example.com","xPathRules":"{\"rules\":[{\"name\":\"title\",\"xpath\":\"//h1/text()\",\"type\":\"single\"}]}","isActive":true}')
check "创建配置" '"success":true' "$result"
CONFIG_ID=$(echo $result | grep -o '"id":[0-9]*' | head -1 | grep -o '[0-9]*')

# 创建带属性选择器的配置
result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"链接提取","targetUrl":"https://example.com","xPathRules":"{\"rules\":[{\"name\":\"link\",\"xpath\":\"//a/@href\",\"type\":\"single\"}]}","isActive":true}')
check "创建属性选择器配置" '"success":true' "$result"
ATTR_CONFIG_ID=$(echo $result | grep -o '"id":[0-9]*' | head -1 | grep -o '[0-9]*')

# 验证参数校验
result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"","targetUrl":"https://example.com","xPathRules":"{}","isActive":true}')
check "空名称校验" '"success":false' "$result"

result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"test","targetUrl":"invalid","xPathRules":"{}","isActive":true}')
check "无效URL校验" '"success":false' "$result"

# 获取配置
result=$(curl -s $BASE_URL/api/configs/$CONFIG_ID)
check "获取配置" '"success":true' "$result"

result=$(curl -s $BASE_URL/api/configs/99999)
check "获取不存在配置" '"success":false' "$result"

# 执行抓取
result=$(curl -s -X POST $BASE_URL/api/scrape/$CONFIG_ID)
check "执行抓取" '"success":true' "$result"

# 属性选择器抓取
result=$(curl -s -X POST $BASE_URL/api/scrape/$ATTR_CONFIG_ID)
check "属性选择器抓取" '"success":true' "$result"

# 验证去重
result=$(curl -s -X POST $BASE_URL/api/scrape/$ATTR_CONFIG_ID)
check "去重验证" '"duplicateRecords":1' "$result"

# 验证属性提取
result=$(curl -s $BASE_URL/api/data/$ATTR_CONFIG_ID)
check "属性值提取" '"link":' "$result"

# 获取数据
result=$(curl -s "$BASE_URL/api/data?page=1&pageSize=10")
check "获取数据列表" '"totalCount":' "$result"

# 获取日志
result=$(curl -s "$BASE_URL/api/logs")
check "获取日志" '"totalCount":' "$result"

# 批量抓取
result=$(curl -s -X POST $BASE_URL/api/scrape/all)
check "批量抓取" '"success":true' "$result"

# 更新配置
result=$(curl -s -X PUT $BASE_URL/api/configs/$CONFIG_ID \
  -H "Content-Type: application/json" \
  -d '{"name":"已更新名称"}')
check "更新配置" '"已更新名称"' "$result"

# 删除配置
result=$(curl -s -X DELETE $BASE_URL/api/configs/$CONFIG_ID)
check "删除配置" '"success":true' "$result"

echo ""
echo "=========================================="
echo "测试完成: ✅ $PASS 通过, ❌ $FAIL 失败"
echo "=========================================="

# 清理：删除测试创建的配置
curl -s -X DELETE $BASE_URL/api/configs/$ATTR_CONFIG_ID > /dev/null 2>&1

exit $FAIL
```

**执行方式**：

```bash
chmod +x test.sh
./test.sh
```

**预期结果**：所有测试项显示 ✅，最终输出 `✅ X 通过, ❌ 0 失败`

## 题目内容

> 使用 .NET 9 + Mini API + EF Core + SQLite 生成一个抓取网页的通用框架，配置网页 URL 及对应抓取的 XPath，数据整理后保存至数据库，只保存新增加数据，已存在不保存，所有代码都在一个类文件里。纯后端，无需页面。

---

## 项目介绍

### 核心功能

1. **配置管理**：动态管理抓取配置（URL + XPath 规则）
2. **网页抓取**：使用 HtmlAgilityPack 解析 HTML
3. **数据提取**：支持单值和列表两种 XPath 提取模式
4. **智能去重**：基于 SHA256 哈希自动识别重复数据
5. **日志记录**：完整的抓取执行日志

### API 接口列表

#### 配置管理

| Method | Endpoint            | 说明         |
| ------ | ------------------- | ------------ |
| GET    | `/api/configs`      | 获取所有配置 |
| GET    | `/api/configs/{id}` | 获取单个配置 |
| POST   | `/api/configs`      | 创建配置     |
| PUT    | `/api/configs/{id}` | 更新配置     |
| DELETE | `/api/configs/{id}` | 删除配置     |

#### 抓取任务

| Method | Endpoint                 | 说明                 |
| ------ | ------------------------ | -------------------- |
| POST   | `/api/scrape/{configId}` | 执行单个配置抓取     |
| POST   | `/api/scrape/all`        | 执行所有启用配置抓取 |

#### 数据查询

| Method | Endpoint               | 说明                 |
| ------ | ---------------------- | -------------------- |
| GET    | `/api/data`            | 获取所有数据（分页） |
| GET    | `/api/data/{configId}` | 获取指定配置数据     |
| DELETE | `/api/data/{id}`       | 删除数据             |

#### 日志查询

| Method | Endpoint    | 说明         |
| ------ | ----------- | ------------ |
| GET    | `/api/logs` | 获取抓取日志 |

### XPath 规则配置示例

#### 单值提取

```json
{
  "rules": [
    {
      "name": "title",
      "xpath": "//h1/text()",
      "type": "single"
    },
    {
      "name": "description",
      "xpath": "//meta[@name='description']/@content",
      "type": "single"
    }
  ]
}
```

#### 列表提取

```json
{
  "rules": [
    {
      "name": "articles",
      "xpath": "//div[@class='article']",
      "type": "list",
      "children": [
        {
          "name": "title",
          "xpath": ".//h2/text()"
        },
        {
          "name": "link",
          "xpath": ".//a/@href"
        },
        {
          "name": "summary",
          "xpath": ".//p[@class='summary']/text()"
        }
      ]
    }
  ]
}
```

### 使用示例

#### 1. 创建抓取配置

```bash
curl -X POST http://localhost:8082/api/configs \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Hacker News",
    "targetUrl": "https://news.ycombinator.com/",
    "xPathRules": "{\"rules\":[{\"name\":\"items\",\"xpath\":\"//tr[@class=\\\"athing\\\"]\",\"type\":\"list\",\"children\":[{\"name\":\"title\",\"xpath\":\".//span[@class=\\\"titleline\\\"]/a/text()\"},{\"name\":\"link\",\"xpath\":\".//span[@class=\\\"titleline\\\"]/a/@href\"}]}]}",
    "isActive": true
  }'
```

#### 2. 执行抓取

```bash
curl -X POST http://localhost:8082/api/scrape/1
```

#### 3. 查看抓取数据

```bash
curl http://localhost:8082/api/data?configId=1
```

### 技术架构

```
┌─────────────────────────────────────────────────────────────┐
│                        Mini API Layer                        │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────────────┐ │
│  │ Configs │  │ Scrape  │  │  Data   │  │      Logs       │ │
│  └────┬────┘  └────┬────┘  └────┬────┘  └────────┬────────┘ │
└───────┼────────────┼────────────┼────────────────┼──────────┘
        │            │            │                │
        └────────────┴────────────┴────────────────┘
                            │
        ┌───────────────────┼───────────────────┐
        │                   ▼                   │
        │         ┌─────────────────┐          │
        │         │ ScraperService  │          │
        │         │  • FetchHtml    │          │
        │         │  • ExtractData  │          │
        │         │  • ComputeHash  │          │
        │         └────────┬────────┘          │
        │                  │                    │
        │                  ▼                    │
        │  ┌─────────────────────────────────┐ │
        │  │      ScraperDbContext           │ │
        │  │  ┌──────────┐ ┌─────────────┐  │ │
        │  │  │ Configs  │ │ ScrapedData │  │ │
        │  │  └──────────┘ └─────────────┘  │ │
        │  │  ┌──────────┐                   │ │
        │  │  │   Logs   │                   │ │
        │  │  └──────────┘                   │ │
        │  └────────────────┬────────────────┘ │
        │                   │                   │
        │                   ▼                   │
        │           ┌─────────────┐            │
        │           │   SQLite    │            │
        │           │ scraper.db  │            │
        │           └─────────────┘            │
        │                                       │
        └───────────────────────────────────────┘
```

### 项目结构

```
442/
├── backend/
│   ├── Program.cs              # 所有后端代码（单文件，遵循 Prompt 约束）
│   ├── backend.csproj          # 项目配置
│   ├── appsettings.json        # 生产环境配置
│   ├── appsettings.Development.json  # 开发环境配置
│   └── Dockerfile              # Docker 构建文件
├── backend.tests/
│   ├── backend.tests.csproj    # 测试项目配置
│   ├── ScraperServiceTests.cs  # 单元测试
│   └── IntegrationTests.cs     # 集成测试
├── docs/
│   └── project_design.md       # 项目设计文档
├── docker-compose.yml          # Docker Compose 配置
├── test.sh                     # 一键测试脚本
├── WebScraper.sln              # 解决方案文件
├── .gitignore                  # Git 忽略文件
└── README.md                   # 本文件
```

### 单文件架构说明

> ⚠️ **架构决策说明**：本项目将所有代码集中在单一文件中，这是为了**严格遵循原始 Prompt 约束**："所有代码都在一个类文件里"。虽然这违反了通用的工程结构规范（避免代码堆叠），但属于响应用户需求的必要实现方式。

根据 Prompt 约束"**所有代码都在一个类文件里**"，`Program.cs` (~1200 行) 包含：

| 代码区块   | 内容                                        |
| ---------- | ------------------------------------------- |
| 日志配置   | Serilog 配置                                |
| 服务配置   | DI 容器、DbContext、HttpClient              |
| 中间件     | 全局异常处理、请求日志                      |
| API 路由   | 配置管理、抓取任务、数据查询、日志查询      |
| DbContext  | EF Core 数据库上下文                        |
| 实体模型   | ScrapeConfig、ScrapedData、ScrapeLog        |
| XPath 模型 | XPathRuleSet、XPathRule                     |
| DTO        | ApiResponse、PagedResult、各种请求/响应 DTO |
| 异常类     | NotFoundException、ValidationException      |
| 服务       | IScraperService、ScraperService             |

### 运行测试

```bash
# 运行所有测试
dotnet test

# 运行测试并显示详细输出
dotnet test --logger "console;verbosity=detailed"

# 运行特定测试类
dotnet test --filter "FullyQualifiedName~ScraperServiceTests"
```

### 测试覆盖范围

| 测试类型 | 测试内容                                    |
| -------- | ------------------------------------------- |
| 单元测试 | ScraperService 抓取逻辑、数据去重、哈希计算 |
| 集成测试 | API 端点、参数验证、分页、错误处理          |
| 并发测试 | 多请求并发处理                              |

### 日志系统

项目使用 **Serilog** 作为日志框架，提供以下功能：

- **结构化日志**：支持丰富的上下文信息
- **多输出目标**：
  - 控制台：彩色输出，便于开发调试
  - 文件：按天滚动，保留 30 天日志
- **请求日志**：自动记录 HTTP 请求/响应信息
- **线程信息**：包含线程 ID，便于追踪并发问题

**日志文件位置**：`/app/logs/scraper-YYYYMMDD.log`

**日志级别**：

- `Debug`：详细的调试信息（XPath 提取、哈希计算等）
- `Information`：正常操作信息（抓取开始/完成、配置变更等）
- `Warning`：潜在问题（XPath 无匹配、无效输入等）
- `Error`：错误信息（网络失败、解析错误等）

### 数据去重策略

1. 提取数据后**递归排序所有键**（使用 `SortedDictionary`）
2. 排序后序列化为 JSON，确保相同内容产生稳定的字符串
3. 对 JSON 字符串计算 SHA256 哈希
4. 使用组合唯一索引 (ContentHash + ConfigId) 检查重复
5. 仅保存新数据，跳过重复数据

> **稳定哈希**：即使 Dictionary 插入顺序不同，相同内容也会产生相同哈希值

### 已修复的 Bug

| Bug              | 描述                                             | 修复方案                                             |
| ---------------- | ------------------------------------------------ | ---------------------------------------------------- |
| 属性选择器失效   | XPath 中 `/@attr` 属性选择器无法正确提取属性值   | 使用 HtmlAgilityPack 原生 XPath 导航器处理所有表达式 |
| 日志保存失败丢失 | 抓取失败后保存日志时若再次失败，会导致异常被吞掉 | 日志保存使用独立 try-catch，确保主流程不受影响       |
| 错误消息过长     | 错误消息超过数据库字段长度导致保存失败           | 截断错误消息至 2000 字符                             |
| 去重查询效率低   | 每次查询都需要全表扫描                           | 添加 (ContentHash, ConfigId) 组合唯一索引            |
| 哈希不稳定       | Dictionary 键顺序不同导致相同内容产生不同哈希    | 递归排序所有键后再计算哈希                           |
| XPath 语法受限   | 手动字符串分割无法处理复杂 XPath 表达式          | 改用原生 XPathNavigator.Evaluate() 处理              |

### 注意事项

- 抓取目标网站时请遵守 robots.txt 规则
- 建议设置合理的抓取间隔，避免对目标网站造成压力
- 部分网站可能有反爬虫机制，需要根据实际情况调整请求头

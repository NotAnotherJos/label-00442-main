#!/bin/bash
# ============================================================================
# Web Scraper 质检测试脚本
# 用法: chmod +x test.sh && ./test.sh
# ============================================================================

BASE_URL="${1:-http://localhost:8082}"
PASS=0
FAIL=0

# 颜色定义
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

check() {
    local name="$1"
    local expected="$2"
    local result="$3"
    
    if echo "$result" | grep -q "$expected"; then
        echo -e "${GREEN}✅ $name${NC}"
        ((PASS++))
    else
        echo -e "${RED}❌ $name${NC}"
        echo -e "   ${YELLOW}预期包含:${NC} $expected"
        echo -e "   ${YELLOW}实际结果:${NC} $result"
        ((FAIL++))
    fi
}

echo ""
echo "=========================================="
echo "  Web Scraper API 质检测试"
echo "  目标地址: $BASE_URL"
echo "=========================================="
echo ""

# ============================================================================
# 1. 基础检查
# ============================================================================
echo "【1. 基础检查】"

result=$(curl -s $BASE_URL/health 2>/dev/null)
if [ -z "$result" ]; then
    echo -e "${RED}❌ 服务未启动，请先运行 docker-compose up -d${NC}"
    exit 1
fi
check "健康检查" '"status":"healthy"' "$result"

echo ""

# ============================================================================
# 2. 配置管理测试
# ============================================================================
echo "【2. 配置管理测试】"

# 创建配置 - 正常
result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"测试配置-单值模式","targetUrl":"https://example.com","xPathRules":"{\"rules\":[{\"name\":\"title\",\"xpath\":\"//h1/text()\",\"type\":\"single\"},{\"name\":\"desc\",\"xpath\":\"//p/text()\",\"type\":\"single\"}]}","isActive":true}')
check "创建配置 - 单值模式" '"success":true' "$result"
CONFIG_ID_1=$(echo $result | grep -o '"id":[0-9]*' | head -1 | grep -o '[0-9]*')

# 创建配置 - 属性选择器
result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"测试配置-属性选择器","targetUrl":"https://example.com","xPathRules":"{\"rules\":[{\"name\":\"link\",\"xpath\":\"//a/@href\",\"type\":\"single\"},{\"name\":\"title\",\"xpath\":\"//h1/text()\",\"type\":\"single\"}]}","isActive":true}')
check "创建配置 - 属性选择器" '"success":true' "$result"
CONFIG_ID_2=$(echo $result | grep -o '"id":[0-9]*' | head -1 | grep -o '[0-9]*')

# 创建配置 - 空名称（应失败）
result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"","targetUrl":"https://example.com","xPathRules":"{}","isActive":true}')
check "验证 - 空名称拒绝" '"success":false' "$result"

# 创建配置 - 无效URL（应失败）
result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"test","targetUrl":"invalid-url","xPathRules":"{}","isActive":true}')
check "验证 - 无效URL拒绝" '"success":false' "$result"

# 创建配置 - 无效JSON（应失败）
result=$(curl -s -X POST $BASE_URL/api/configs \
  -H "Content-Type: application/json" \
  -d '{"name":"test","targetUrl":"https://example.com","xPathRules":"invalid json","isActive":true}')
check "验证 - 无效JSON拒绝" '"success":false' "$result"

# 获取所有配置
result=$(curl -s $BASE_URL/api/configs)
check "获取所有配置" '"success":true' "$result"

# 获取单个配置
result=$(curl -s $BASE_URL/api/configs/$CONFIG_ID_1)
check "获取单个配置" '"success":true' "$result"

# 获取不存在的配置
result=$(curl -s $BASE_URL/api/configs/99999)
check "获取不存在配置 - 返回404" '"success":false' "$result"

# 更新配置
result=$(curl -s -X PUT $BASE_URL/api/configs/$CONFIG_ID_1 \
  -H "Content-Type: application/json" \
  -d '{"name":"已更新的配置名称","isActive":false}')
check "更新配置" '"已更新的配置名称"' "$result"

echo ""

# ============================================================================
# 3. 抓取功能测试
# ============================================================================
echo "【3. 抓取功能测试】"

# 执行抓取 - 单值模式
result=$(curl -s -X POST $BASE_URL/api/scrape/$CONFIG_ID_1)
check "执行抓取 - 单值模式" '"success":true' "$result"

# 执行抓取 - 属性选择器
result=$(curl -s -X POST $BASE_URL/api/scrape/$CONFIG_ID_2)
check "执行抓取 - 属性选择器" '"success":true' "$result"

# 验证去重 - 再次抓取相同数据
result=$(curl -s -X POST $BASE_URL/api/scrape/$CONFIG_ID_2)
check "去重验证 - 重复数据不保存" '"duplicateRecords":1' "$result"

# 抓取不存在的配置
result=$(curl -s -X POST $BASE_URL/api/scrape/99999)
check "抓取不存在配置 - 返回404" '"success":false' "$result"

# 批量抓取所有启用配置
result=$(curl -s -X POST $BASE_URL/api/scrape/all)
check "批量抓取所有配置" '"success":true' "$result"

echo ""

# ============================================================================
# 4. 数据查询测试
# ============================================================================
echo "【4. 数据查询测试】"

# 获取所有数据（分页）
result=$(curl -s "$BASE_URL/api/data?page=1&pageSize=10")
check "获取数据 - 分页查询" '"totalCount":' "$result"

# 获取指定配置的数据
result=$(curl -s $BASE_URL/api/data/$CONFIG_ID_2)
check "获取指定配置数据" '"success":true' "$result"

# 验证属性选择器提取结果
result=$(curl -s $BASE_URL/api/data/$CONFIG_ID_2)
check "验证属性值提取 (/@href Bug修复)" 'link' "$result"

# 获取不存在配置的数据
result=$(curl -s $BASE_URL/api/data/99999)
check "获取不存在配置数据 - 返回404" '"success":false' "$result"

echo ""

# ============================================================================
# 5. 日志查询测试
# ============================================================================
echo "【5. 日志查询测试】"

# 获取所有日志
result=$(curl -s "$BASE_URL/api/logs?page=1&pageSize=10")
check "获取日志 - 分页查询" '"totalCount":' "$result"

# 获取指定配置的日志
result=$(curl -s "$BASE_URL/api/logs?configId=$CONFIG_ID_2")
check "获取指定配置日志" '"success":true' "$result"

echo ""

# ============================================================================
# 6. 删除功能测试
# ============================================================================
echo "【6. 删除功能测试】"

# 获取数据ID用于删除测试
DATA_ID=$(curl -s "$BASE_URL/api/data?page=1&pageSize=1" | grep -o '"id":[0-9]*' | head -1 | grep -o '[0-9]*')

if [ -n "$DATA_ID" ]; then
    # 删除数据
    result=$(curl -s -X DELETE $BASE_URL/api/data/$DATA_ID)
    check "删除单条数据" '"success":true' "$result"
else
    echo -e "${YELLOW}⚠️  跳过数据删除测试（无数据）${NC}"
fi

# 删除配置（级联删除）
result=$(curl -s -X DELETE $BASE_URL/api/configs/$CONFIG_ID_1)
check "删除配置 - 级联删除" '"success":true' "$result"

# 清理测试数据
curl -s -X DELETE $BASE_URL/api/configs/$CONFIG_ID_2 > /dev/null 2>&1

echo ""

# ============================================================================
# 测试结果汇总
# ============================================================================
echo "=========================================="
if [ $FAIL -eq 0 ]; then
    echo -e "  ${GREEN}测试完成: ✅ $PASS 通过, ❌ $FAIL 失败${NC}"
    echo -e "  ${GREEN}所有测试通过！${NC}"
else
    echo -e "  ${RED}测试完成: ✅ $PASS 通过, ❌ $FAIL 失败${NC}"
    echo -e "  ${RED}存在失败项，请检查！${NC}"
fi
echo "=========================================="
echo ""

exit $FAIL

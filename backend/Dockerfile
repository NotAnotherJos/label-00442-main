# ============================================================================
# Web Scraper Backend Dockerfile
# 支持 ARM64 和 AMD64 跨平台构建
# ============================================================================

# 构建阶段
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
WORKDIR /src

# 复制项目文件并还原依赖
COPY backend.csproj .
RUN dotnet restore -a $TARGETARCH

# 复制源代码并构建
COPY . .
RUN dotnet publish -a $TARGETARCH -c Release -o /app/publish --no-restore

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# 创建数据目录和日志目录
RUN mkdir -p /app/data /app/logs

# 设置环境变量
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# 从构建阶段复制发布文件
COPY --from=build /app/publish .

# 健康检查
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# 暴露端口
EXPOSE 8080

# 启动应用
ENTRYPOINT ["dotnet", "backend.dll"]

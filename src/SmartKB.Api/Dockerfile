# ---------- 构建阶段 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先还原（依赖层缓存；只还原 API 及其依赖链，部署包不含 tests）
COPY SmartKB.slnx ./
COPY src/SmartKB.Domain/SmartKB.Domain.csproj src/SmartKB.Domain/
COPY src/SmartKB.Infrastructure/SmartKB.Infrastructure.csproj src/SmartKB.Infrastructure/
COPY src/SmartKB.AI/SmartKB.AI.csproj src/SmartKB.AI/
COPY src/SmartKB.Application/SmartKB.Application.csproj src/SmartKB.Application/
COPY src/SmartKB.Api/SmartKB.Api.csproj src/SmartKB.Api/
RUN dotnet restore src/SmartKB.Api/SmartKB.Api.csproj

# 再编译发布
COPY src/ src/
RUN dotnet publish src/SmartKB.Api -c Release -o /app/publish --no-restore

# ---------- 运行阶段 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# 容器内监听 8080（非 root 端口）
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# 文档存储卷（上传的原始文件）
VOLUME /app/storage

ENTRYPOINT ["dotnet", "SmartKB.Api.dll"]

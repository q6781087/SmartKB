using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using SmartKB.AI;
using SmartKB.Api;
using SmartKB.Application;
using SmartKB.Application.Services;
using SmartKB.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ---------- Serilog 全链路日志 ----------
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "SmartKB"));

// ---------- 分层注册 ----------
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddAiInfrastructure(builder.Configuration);

// ---------- OpenTelemetry（全链路追踪：HTTP 入站/出站 + Npgsql） ----------
// 未配置 OpenTelemetry:OtlpEndpoint 时不导出，零开销
var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
if (!string.IsNullOrEmpty(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("SmartKB.Api"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddNpgsql()
            .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));
}

// ---------- JWT 认证 ----------
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtSettings>()
    ?? throw new InvalidOperationException("缺少 Jwt 配置节");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey))
        };
        opt.MapInboundClaims = true; // sub -> ClaimTypes.NameIdentifier

        // SignalR 走 query string 传 token（WebSocket 不支持自定义 header）
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.Request.Path.StartsWithSegments("/hubs/chat"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ---------- Web ----------
builder.Services.AddControllers();
builder.Services.AddSignalR(); // M3 接入聊天 Hub，M1 先注册
builder.Services.AddOpenApi();
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p => p
    .AllowAnyHeader().AllowAnyMethod()
    .SetIsOriginAllowed(_ => true) // 生产环境由配置收窄
    .AllowCredentials()));

var app = builder.Build();

// ---------- 启动初始化：迁移 + 种子 ----------
using (var scope = app.Services.CreateScope())
{
    var adminPwd = app.Configuration["SmartKB:AdminInitialPassword"]
        ?? throw new InvalidOperationException("缺少 SmartKB:AdminInitialPassword 配置");
    await scope.ServiceProvider.SeedAsync(adminPwd);
}

// ---------- 中间件 ----------
app.UseSerilogRequestLogging();
app.UseCors();

// 业务异常 → 400 {message}
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (BusinessRuleException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "未处理异常 {TraceId}", ctx.TraceIdentifier);
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new { message = "服务器内部错误", traceId = ctx.TraceIdentifier });
    }
});

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json
}

app.MapControllers();

// 问答流式 Hub（SignalR 打字机输出）
app.MapHub<SmartKB.Api.Hubs.ChatHub>("/hubs/chat");

// 健康检查（M5 扩展为分项检查：DB / 向量索引 / LLM 连通性）
app.MapGet("/health", async (SmartKbDbContext db) =>
{
    var canConnect = await db.Database.CanConnectAsync();
    return canConnect
        ? Results.Ok(new { status = "healthy", db = "ok" })
        : Results.Problem("数据库连接失败", statusCode: 503);
});

app.Run();

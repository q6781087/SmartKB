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
using SmartKB.Api.Filters;
using SmartKB.Api.Gateway;
using SmartKB.Api.Health;
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

// 当前用户抽象：让 Application 服务层能自己设防（对象级授权守卫依赖它）
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// 分项健康检查（/health）；LLM 探活单独一个短超时客户端，不复用业务 LLM 客户端
builder.Services.AddHttpClient("health-llm", c => c.Timeout = TimeSpan.FromSeconds(4));
builder.Services.AddSingleton<HealthCheckCache>();
builder.Services.AddScoped<IHealthProbe, HealthProbe>();

// OpenAI 兼容网关（/v1/*）：默认关闭；ApiKey 只从环境变量 Gateway__ApiKey 注入
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
// LDAP/AD 可选登录：默认关闭；绑定密码走环境变量（如 LDAP_BIND_PASSWORD）
builder.Services.Configure<LdapOptions>(builder.Configuration.GetSection("Ldap"));
builder.Services.AddSingleton<ILdapDirectory, LdapDirectoryService>();
builder.Services.AddScoped<IGatewayAuthenticator, GatewayAuthenticator>();

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
// 全局审计过滤器：自动记录 /api 下所有写操作，业务服务零侵入
builder.Services.AddControllers(options => options.Filters.Add<AuditLogFilter>());
builder.Services.AddSignalR();
builder.Services.AddOpenApi();

// CORS：生产由 Nginx 同源反代，默认不开放任何跨域来源；
// 开发环境（Vite :5173）在 appsettings.Development.json 配白名单。
var corsOrigins = builder.Configuration.GetSection("SmartKB:CorsAllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
{
    p.AllowAnyHeader().AllowAnyMethod();
    if (corsOrigins.Length > 0)
        p.WithOrigins(corsOrigins).AllowCredentials();
    else
        p.SetIsOriginAllowed(_ => false);
}));

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

// 业务异常 → 400 {message}；越权 → 403 {message}；其余 → 500（带 traceId）
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ForbiddenException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { message = ex.Message });
    }
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

// 分项健康检查：DB / pgvector 扩展与索引 / 文件存储可写 / LLM 连通性
// unhealthy（DB、存储故障）→ 503；degraded（LLM 抖动、索引缺失）→ 200，避免误触发容器重启
app.MapGet("/health", async (IHealthProbe probe, HttpContext ctx) =>
{
    var report = await probe.CheckAsync(ctx.RequestAborted);
    return Results.Json(report, statusCode: report.Status == "unhealthy"
        ? StatusCodes.Status503ServiceUnavailable
        : StatusCodes.Status200OK);
}).AllowAnonymous();

app.Run();

using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Options;
using SmartKB.Application;

namespace SmartKB.Api;

/// <summary>
/// LDAP/AD 目录实现（search-then-bind，AD/OpenLDAP 通用的验证方式）：
/// 1. 绑定服务账号（或匿名）；2. 按过滤器搜索用户 DN；3. 以用户 DN + 用户密码二次绑定验证。
/// 任何网络/配置异常都折叠为 null——登录失败不能向外暴露域控细节。
/// </summary>
public class LdapDirectoryService(IOptions<LdapOptions> options, ILogger<LdapDirectoryService> logger) : ILdapDirectory
{
    public Task<LdapUserInfo?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        var opt = options.Value;
        if (!opt.Enabled || string.IsNullOrWhiteSpace(opt.Server) || string.IsNullOrWhiteSpace(opt.BaseDn))
            return Task.FromResult<LdapUserInfo?>(null);

        // LDAP 过滤器注入防护：RFC 4515 特殊字符转义（* ( ) \ NUL）
        var safeUsername = EscapeFilterValue(username.Trim());
        // 空过滤器配置回退 AD 默认（compose 插值不支持嵌套 {0}，默认值留空交给代码兜底）
        var filterTemplate = string.IsNullOrWhiteSpace(opt.SearchFilter)
            ? "(&(objectClass=user)(sAMAccountName={0}))"
            : opt.SearchFilter;
        var filter = filterTemplate.Replace("{0}", safeUsername);

        try
        {
            using var connection = CreateConnection(opt);
            // 1. 服务账号绑定（未配置则匿名绑定，适用于允许匿名搜索的目录）
            if (!string.IsNullOrWhiteSpace(opt.BindDn))
                connection.Bind(new NetworkCredential(opt.BindDn, opt.BindPassword ?? string.Empty));
            else
                connection.Bind(new NetworkCredential(string.Empty, string.Empty));

            // 2. 搜索用户 DN
            var request = new SearchRequest(
                opt.BaseDn, filter, SearchScope.Subtree, opt.DisplayNameAttribute);
            request.SizeLimit = 1;
            request.TimeLimit = TimeSpan.FromSeconds(5);

            var response = (SearchResponse)connection.SendRequest(request);
            if (response.Entries.Count == 0)
                return Task.FromResult<LdapUserInfo?>(null);

            var entry = response.Entries[0];
            var displayName = entry.Attributes.Contains(opt.DisplayNameAttribute)
                ? entry.Attributes[opt.DisplayNameAttribute][0]?.ToString()
                : null;

            // 3. 以用户 DN + 用户密码二次绑定——这一步才是真正的密码验证。
            //    凭据错误时 Bind() 直接抛 LdapException(49)，走到这里即验证通过。
            var userDn = entry.DistinguishedName;
            using var userConnection = CreateConnection(opt);
            userConnection.Bind(new NetworkCredential(userDn, password));

            ct.ThrowIfCancellationRequested();
            return Task.FromResult<LdapUserInfo?>(
                new LdapUserInfo(username.Trim(), string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName));
        }
        catch (LdapException ex)
        {
            // 凭据错误（49）是正常失败；其余记录警告便于排查域控连通性
            if (ex.ErrorCode != 49)
                logger.LogWarning(ex, "LDAP 认证异常 Server={Server} ErrorCode={Code}", opt.Server, ex.ErrorCode);
            return Task.FromResult<LdapUserInfo?>(null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LDAP 连接失败 Server={Server}:{Port}", opt.Server, opt.Port);
            return Task.FromResult<LdapUserInfo?>(null);
        }
    }

    private static LdapConnection CreateConnection(LdapOptions opt)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(opt.Server, opt.Port, fullyQualifiedDnsHostName: true, connectionless: false))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        connection.SessionOptions.ProtocolVersion = 3;
        if (opt.UseSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
            connection.AuthType = AuthType.Negotiate;
        }
        else
        {
            connection.AuthType = AuthType.Negotiate;
        }
        return connection;
    }

    /// <summary>RFC 4515 过滤器值转义</summary>
    public static string EscapeFilterValue(string value) => value
        .Replace(@"\", @"\5c")
        .Replace("*", @"\2a")
        .Replace("(", @"\28")
        .Replace(")", @"\29")
        .Replace("\0", @"\00");
}

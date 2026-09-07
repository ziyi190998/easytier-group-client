using System.Runtime.InteropServices;
using EasyTierGroupClient.Util;

namespace EasyTierGroupClient.Services;

/// <summary>群友互访隔离：连接时添加防火墙规则，阻止与其他群友虚拟网段（10.144.0.100~109）的双向通信；
/// 不影响对开服服务器（10.144.0.1）与中继（10.144.0.254）的访问。断开时移除。
/// 通过 Windows 防火墙 COM API + dynamic 晚绑定操作，不执行外部命令。
/// EasyTier 自身对等流量走物理网卡公网地址，不受影响。</summary>
public static class FirewallManager
{
    private const string InRuleName = "EasyTierGroupClient-隔离-入站";
    private const string OutRuleName = "EasyTierGroupClient-隔离-出站";
    private const string RemoteRange = "10.144.0.100-10.144.0.200";

    private const int DirIn = 1;    // NET_FW_RULE_DIR_IN
    private const int DirOut = 2;   // NET_FW_RULE_DIR_OUT
    private const int ActionBlock = 0;          // NET_FW_ACTION_BLOCK
    private const int ProtocolAny = 256;        // NET_FW_IP_PROTOCOL_ANY
    private const int ProfileAll = 0x7FFFFFFF;  // 所有配置文件

    // ProgID：Windows 防火墙 COM 对象（微软文档标准方式）
    private const string PolicyProgId = "HNetCfg.FwPolicy2";
    private const string RuleProgId = "HNetCfg.FwRule";

    private static object CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"无法创建防火墙 COM 对象 {progId}"));

    public static void ApplyIsolation()
    {
        RemoveIsolation(); // 幂等：先清理可能残留的旧规则
        AddRule(InRuleName, DirIn);
        AddRule(OutRuleName, DirOut);
        SimpleLog.Info("防火墙互访隔离规则已生效");
    }

    public static void RemoveIsolation()
    {
        RemoveRule(InRuleName);
        RemoveRule(OutRuleName);
    }

    private static void AddRule(string name, int direction)
    {
        try
        {
            dynamic policy = CreateCom(PolicyProgId);
            dynamic rule = CreateCom(RuleProgId);
            rule.Name = name;
            rule.Description = "EasyTier 群友客户端：阻止群友之间互访（保留对服务器 10.144.0.1 的访问）";
            rule.Direction = direction;
            rule.Action = ActionBlock;
            rule.Protocol = ProtocolAny;
            rule.RemoteAddresses = RemoteRange;
            rule.Enabled = true;
            rule.Profiles = ProfileAll;
            policy.Rules.Add(rule);
        }
        catch (Exception ex)
        {
            SimpleLog.Error($"添加防火墙规则失败：{name}", ex);
        }
    }

    private static void RemoveRule(string name)
    {
        try
        {
            dynamic policy = CreateCom(PolicyProgId);
            policy.Rules.Remove(name); // 规则不存在时抛 COMException，忽略即可
        }
        catch (COMException)
        {
            // 规则不存在，无需处理
        }
        catch (Exception ex)
        {
            SimpleLog.Error($"移除防火墙规则失败：{name}", ex);
        }
    }
}

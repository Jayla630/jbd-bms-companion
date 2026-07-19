using System.Diagnostics;

namespace Jbd.Protocol.Tests;

/// <summary>
/// 第 1 层验证（工单六）：通过 sim_bridge.py 把抓取会话直接接到真 Python 模拟器的
/// Device.handle_request 上，验证模式门链路"进模式前 0xAA 回 0x80 → 进模式后回 0x00 →
/// 写退后门重新关上"，以及整段扫读与抓取档生成不崩。
/// 模拟器没有真值：0x10~0xA2 未实现恒回 0x80，这里只证管道，真值只能来自真板（第 2 层）。
/// 本机无 python 或找不到 simulator 目录时静默跳过（不算失败）。
/// </summary>
public class SimulatorIntegrationTests
{
    [Fact]
    public void CaptureAgainstPythonSimulator_ModeGateChainAndDumpWork()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return; // 测试从非仓库布局跑（如裸 CI 缓存目录）：跳过
        }

        string bridge = Path.Combine(
            repoRoot, "upper-computer", "tests", "Jbd.Protocol.Tests", "sim_bridge.py");
        string simulatorDir = Path.Combine(repoRoot, "simulator");

        using var process = StartBridge(bridge, simulatorDir);
        if (process is null)
        {
            return; // 本机没有 python：跳过
        }

        try
        {
            Func<byte[], byte[]?> exchange = request =>
            {
                // 桥对每个输入行必回一行（无响应=空行）；桥进程死亡时 ReadLine 返回 null
                process.StandardInput.WriteLine(Convert.ToHexString(request));
                string? line = process.StandardOutput.ReadLine();
                return string.IsNullOrEmpty(line) ? null : Convert.FromHexString(line);
            };

            var report = ConfigModeCapture.Run(exchange, writeExit: true);

            // 身份戳：0x05 不受配置模式限制，进模式前即可读
            Assert.True(report.DeviceName!.IsOk);
            Assert.Equal("JBD-SP04S010-Sim", System.Text.Encoding.ASCII.GetString(report.DeviceName.Data));

            // 模式门链路：进模式前 0xAA 被 0x80 守卫拦下，进模式后放行
            Assert.Equal(0x80, report.GatePreErrorCounts!.Status);
            Assert.True(report.EnterAccepted);
            Assert.Equal(0x00, report.GatePostErrorCounts!.Status);
            Assert.True(report.GateProven);

            // 整段扫完不崩：模拟器未实现 0x10~0xA2（恒回 0x80），0xAA 有占位数据
            Assert.Equal(JbdConfigMode.ScanRegisters.Count, report.ScanEntries.Count);
            Assert.All(report.ScanEntries, e => Assert.Equal(CaptureOutcome.Ok, e.Outcome));
            var errorCounts = report.ScanEntries[^1];
            Assert.True(errorCounts.IsOk);
            Assert.NotEmpty(errorCounts.Data);

            // 写退后门重新关上：0xAA 复测回 0x80，设备已退出配置模式
            Assert.True(report.ExitAck!.IsOk);
            Assert.Equal(0x80, report.PostExitProbe!.Status);

            // 抓取档能生成
            string markdown = report.ToMarkdown();
            Assert.Contains("JBD-SP04S010-Sim", markdown);
            Assert.Contains("模式门自检：通过", markdown);
        }
        finally
        {
            try
            {
                process.StandardInput.WriteLine("quit");
                if (!process.WaitForExit(3000))
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
                // 清理失败不影响断言结果
            }
        }
    }

    /// <summary>从测试输出目录向上找同时含 simulator 与 upper-computer 的目录（仓库根）。</summary>
    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "simulator")) &&
                Directory.Exists(Path.Combine(dir.FullName, "upper-computer")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static Process? StartBridge(string bridgeScript, string simulatorDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(bridgeScript);
        startInfo.ArgumentList.Add(simulatorDir);

        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

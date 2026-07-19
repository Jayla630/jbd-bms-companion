namespace Jbd.Protocol.Tests;

/// <summary>
/// 配置模式抓取会话（ConfigModeCapture）对假设备的管道测试：
/// 模式门自检、无应答地址不中断扫描、抓取档 markdown 生成、进模式被拒时不扫描。
/// 假设备复刻模拟器的门语义（0xAA 受 0x80 守卫），另设 0x11 恒无应答制造超时路径。
/// 真值验证走模拟器集成测试与真板（工单六两层），这里只证管道对。
/// </summary>
public class ConfigModeCaptureTests
{
    /// <summary>最小假设备：进模式魔数校验 + 0x80 守卫 + 指定寄存器无应答。</summary>
    private sealed class FakeDevice
    {
        public bool ConfigMode { get; private set; }

        public bool RejectEnter { get; init; }

        public byte[]? Handle(byte[] request)
        {
            byte op = request[1];
            byte register = request[2];
            if (op == 0x5A && register == JbdConfigMode.RegEnterConfig)
            {
                if (RejectEnter || request[4] != 0x56 || request[5] != 0x78)
                {
                    return BuildResponse(register, 0x80, []);
                }

                ConfigMode = true;
                return BuildResponse(register, 0x00, []);
            }

            if (op == 0x5A && register == JbdConfigMode.RegExitSave)
            {
                ConfigMode = false;
                return BuildResponse(register, 0x00, []);
            }

            return register switch
            {
                JbdConfigMode.RegDeviceName => BuildResponse(register, 0x00, "SIM"u8.ToArray()),
                0x11 => null, // 恒无应答：制造超时路径
                JbdConfigMode.ScanStart or JbdConfigMode.RegErrorCounts when ConfigMode
                    => BuildResponse(register, 0x00, [0x12, 0x34]),
                _ => BuildResponse(register, 0x80, []),
            };
        }
    }

    private static byte[] BuildResponse(byte register, byte status, byte[] data)
    {
        ushort checksum = JbdChecksum.ComputeResponse(status, data);
        byte[] frame = new byte[data.Length + JbdFrame.ResponseOverhead];
        frame[0] = JbdFrame.Start;
        frame[1] = register;
        frame[2] = status;
        frame[3] = (byte)data.Length;
        data.CopyTo(frame, 4);
        frame[^3] = (byte)(checksum >> 8);
        frame[^2] = (byte)(checksum & 0xFF);
        frame[^1] = JbdFrame.End;
        return frame;
    }

    [Fact]
    public void Run_GateFlipsFrom0x80To0x00_GateProven()
    {
        var device = new FakeDevice();

        var report = ConfigModeCapture.Run(device.Handle, writeExit: false);

        Assert.True(report.EnterAccepted);
        Assert.Equal(0x80, report.GatePreErrorCounts!.Status);
        Assert.Equal(0x00, report.GatePostErrorCounts!.Status);
        Assert.True(report.GateProven);
    }

    [Fact]
    public void Run_NoResponseRegister_RecordedAndScanContinuesToEnd()
    {
        var device = new FakeDevice();

        var report = ConfigModeCapture.Run(device.Handle, writeExit: false);

        Assert.Equal(JbdConfigMode.ScanRegisters.Count, report.ScanEntries.Count);
        var silent = report.ScanEntries.Single(e => e.Register == 0x11);
        Assert.Equal(CaptureOutcome.NoResponse, silent.Outcome);
        // 无应答之后的地址照常扫到，且 0xAA 排在最后
        Assert.Equal(0x12, report.ScanEntries[2].Register);
        Assert.Equal(JbdConfigMode.RegErrorCounts, report.ScanEntries[^1].Register);
    }

    [Fact]
    public void Run_WriteExit_ExitAckedAndPostProbeShowsGateClosed()
    {
        var device = new FakeDevice();

        var report = ConfigModeCapture.Run(device.Handle, writeExit: true);

        Assert.True(report.ExitAck!.IsOk);
        Assert.False(device.ConfigMode);
        Assert.Equal(0x80, report.PostExitProbe!.Status); // 退出后门重新关上
    }

    [Fact]
    public void Run_NoWriteExit_NoExitFrameSent()
    {
        var device = new FakeDevice();

        var report = ConfigModeCapture.Run(device.Handle, writeExit: false);

        Assert.Null(report.ExitAck);
        Assert.Null(report.PostExitProbe);
        Assert.True(device.ConfigMode); // 会话没写退出帧，门仍开着（断连退法留给传输层/真机核）
    }

    [Fact]
    public void Run_EnterRejected_ScanIsSkipped()
    {
        var device = new FakeDevice { RejectEnter = true };

        var report = ConfigModeCapture.Run(device.Handle, writeExit: true);

        Assert.False(report.EnterAccepted);
        Assert.Empty(report.ScanEntries);
        Assert.Null(report.ExitAck); // 没进成模式，也就不写退出
        Assert.Contains("扫读未执行", report.ToMarkdown());
    }

    [Fact]
    public void Run_EnterNoResponse_ScanIsSkipped()
    {
        var device = new FakeDevice();
        Func<byte[], byte[]?> exchange = request =>
            request[2] == JbdConfigMode.RegEnterConfig ? null : device.Handle(request);

        var report = ConfigModeCapture.Run(exchange, writeExit: false);

        Assert.False(report.EnterAccepted);
        Assert.Empty(report.ScanEntries);
    }

    [Fact]
    public void Run_ProgressReportsEveryRegister()
    {
        var device = new FakeDevice();
        var calls = new List<(int Done, int Total)>();

        ConfigModeCapture.Run(device.Handle, writeExit: false, (done, total) => calls.Add((done, total)));

        Assert.Equal(JbdConfigMode.ScanRegisters.Count, calls.Count);
        Assert.Equal((calls.Count, calls.Count), calls[^1]);
    }

    [Fact]
    public void ToMarkdown_ContainsHeaderRawRowsAndNoResponseRow()
    {
        var device = new FakeDevice();
        var report = ConfigModeCapture.Run(device.Handle, writeExit: true);
        report.Link = "COM-TEST @ 9600 8N1";

        string md = report.ToMarkdown();

        Assert.Contains("`SIM`", md);                        // 设备名身份戳
        Assert.Contains("COM-TEST @ 9600 8N1", md);          // 链路
        Assert.Contains("模式门自检：通过", md);
        Assert.Contains("| 0x10 | 0x00 | 2 | 1234 |", md);   // 原始行：寄存器|状态|长度|hex
        Assert.Contains("| 0x11 | 无应答 | – | – |", md);
        Assert.Contains("退出后 0xAA 探测：状态 0x80", md);
    }
}

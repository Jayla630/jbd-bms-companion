using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows;
using System.Windows.Threading;
using Jbd.Protocol;
using Jbd.UpperComputer.Services;
using Prism.Commands;
using Prism.Mvvm;

namespace Jbd.UpperComputer.ViewModels;

public enum ConnectionState
{
    Disconnected,
    Connected,
    Communicating,
    Timeout,
}

/// <summary>单体电压一行的展示项。</summary>
public sealed class CellReading : BindableBase
{
    private double _voltageV;

    public CellReading(int index) => Label = $"Cell {index}";

    public string Label { get; }

    public double VoltageV
    {
        get => _voltageV;
        set => SetProperty(ref _voltageV, value);
    }
}

public class MainViewModel : BindableBase, IDisposable
{
    private readonly ISerialBmsClient _client;
    private readonly Dispatcher _dispatcher;
    private string? _selectedPort;
    private int _baudRate = 9600;
    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private double _totalVoltageV;
    private double _currentA;
    private int _socPercent;
    private DateTime? _lastUpdated;
    private string? _errorMessage;
    private bool _chargeMosOn;
    private bool _dischargeMosOn;
    private bool _balanceOn;
    private bool _pendingBalanceRequest;
    private bool _isMosLocked;

    public MainViewModel(ISerialBmsClient client)
    {
        _client = client;
        _dispatcher = Application.Current.Dispatcher;
        _client.BasicInfoReceived += OnBasicInfoReceived;
        _client.CellVoltagesReceived += OnCellVoltagesReceived;
        _client.WriteAcknowledged += OnWriteAcknowledged;
        _client.ResponseTimedOut += OnResponseTimedOut;

        RefreshPortsCommand = new DelegateCommand(RefreshPorts);
        ConnectCommand = new DelegateCommand(Connect, CanConnect)
            .ObservesProperty(() => SelectedPort)
            .ObservesProperty(() => ConnectionState);
        DisconnectCommand = new DelegateCommand(Disconnect, () => IsConnected)
            .ObservesProperty(() => ConnectionState);
        RefreshPorts();
    }

    public ObservableCollection<string> AvailablePorts { get; } = [];

    public ObservableCollection<CellReading> Cells { get; } =
        [new(1), new(2), new(3), new(4)];

    /// <summary>当前置位的保护项可读标签（映射逻辑在 Jbd.Protocol，UI 只展示）。</summary>
    public ObservableCollection<string> ActiveProtections { get; } = [];

    public DelegateCommand RefreshPortsCommand { get; }

    public DelegateCommand ConnectCommand { get; }

    public DelegateCommand DisconnectCommand { get; }

    public string? SelectedPort
    {
        get => _selectedPort;
        set => SetProperty(ref _selectedPort, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetProperty(ref _connectionState, value))
            {
                RaisePropertyChanged(nameof(ConnectionStateText));
            }
        }
    }

    public string ConnectionStateText => ConnectionState switch
    {
        ConnectionState.Disconnected => "未连接",
        ConnectionState.Connected => "已连接",
        ConnectionState.Communicating => "通信中",
        ConnectionState.Timeout => "超时",
        _ => "未知",
    };

    public double TotalVoltageV
    {
        get => _totalVoltageV;
        private set => SetProperty(ref _totalVoltageV, value);
    }

    public double CurrentA
    {
        get => _currentA;
        private set => SetProperty(ref _currentA, value);
    }

    public int SocPercent
    {
        get => _socPercent;
        private set => SetProperty(ref _socPercent, value);
    }

    public DateTime? LastUpdated
    {
        get => _lastUpdated;
        private set
        {
            if (SetProperty(ref _lastUpdated, value))
            {
                RaisePropertyChanged(nameof(LastUpdatedText));
            }
        }
    }

    public string LastUpdatedText =>
        LastUpdated is { } t ? $"最后刷新：{t:HH:mm:ss.fff}" : "尚未收到数据";

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>
    /// 充电 MOS 开关。显示值来自 0x03 回读的 FET 状态（非乐观更新）：
    /// setter 不改本地值，只把目标状态入队写 0xE1，并立即通知界面弹回当前回读值；
    /// 真正的状态变化等下一轮 0x03 回读驱动。写被设备静默拒绝（如软件锁定）时开关自然弹回。
    /// </summary>
    public bool ChargeMosOn
    {
        get => _chargeMosOn;
        set
        {
            if (_chargeMosOn == value)
            {
                return;
            }

            _client.WriteMosControl(chargeOn: value, dischargeOn: _dischargeMosOn);
            SnapBack(nameof(ChargeMosOn));
        }
    }

    /// <summary>放电 MOS 开关，语义同 <see cref="ChargeMosOn"/>。</summary>
    public bool DischargeMosOn
    {
        get => _dischargeMosOn;
        set
        {
            if (_dischargeMosOn == value)
            {
                return;
            }

            _client.WriteMosControl(chargeOn: _chargeMosOn, dischargeOn: value);
            SnapBack(nameof(DischargeMosOn));
        }
    }

    /// <summary>
    /// 均衡开关（0xE2）。协议的 0x03 响应里没有"均衡使能"回读字段（docs/ 寄存器表，
    /// 偏移 12–15 是逐串均衡动作位图而非使能开关），所以此开关以写 ack 受理为准更新显示，
    /// 仍非乐观：ack 未受理或超时则弹回。
    /// </summary>
    public bool BalanceOn
    {
        get => _balanceOn;
        set
        {
            if (_balanceOn == value)
            {
                return;
            }

            _pendingBalanceRequest = value;
            _client.WriteBalance(value);
            SnapBack(nameof(BalanceOn));
        }
    }

    /// <summary>保护状态 bit12：MOS 软件锁定。锁定时写 0xE1 会被设备静默拒绝，
    /// 本切片只识别并醒目提示（开关保留可拨以演示回读弹回），不做引导式解锁。</summary>
    public bool IsMosLocked
    {
        get => _isMosLocked;
        private set
        {
            if (SetProperty(ref _isMosLocked, value))
            {
                RaisePropertyChanged(nameof(HasNoProtections));
            }
        }
    }

    public bool HasNoProtections => ActiveProtections.Count == 0;

    private bool IsConnected => ConnectionState != ConnectionState.Disconnected;

    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (string port in SerialPort.GetPortNames().Distinct().OrderBy(p => p))
        {
            AvailablePorts.Add(port);
        }

        if (SelectedPort is null || !AvailablePorts.Contains(SelectedPort))
        {
            SelectedPort = AvailablePorts.FirstOrDefault();
        }
    }

    private bool CanConnect() => !IsConnected && !string.IsNullOrEmpty(SelectedPort);

    private void Connect()
    {
        try
        {
            _client.Connect(SelectedPort!, BaudRate);
            ErrorMessage = null;
            ConnectionState = ConnectionState.Connected;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"连接失败：{ex.Message}";
        }
    }

    private void Disconnect()
    {
        _client.Disconnect();
        ConnectionState = ConnectionState.Disconnected;
    }

    public void Dispose()
    {
        _client.BasicInfoReceived -= OnBasicInfoReceived;
        _client.CellVoltagesReceived -= OnCellVoltagesReceived;
        _client.WriteAcknowledged -= OnWriteAcknowledged;
        _client.ResponseTimedOut -= OnResponseTimedOut;
    }

    /// <summary>
    /// 开关 setter 的"弹回"：本地值未变，异步补发一次变更通知，
    /// 让绑定控件回读 getter 恢复显示（同步 Raise 会被 WPF 在绑定更新中忽略）。
    /// </summary>
    private void SnapBack(string propertyName)
        => _dispatcher.BeginInvoke(() => RaisePropertyChanged(propertyName));

    private void OnWriteAcknowledged(byte register, bool accepted)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!accepted)
            {
                ErrorMessage = $"写入 0x{register:X2} 被设备拒绝";
                return;
            }

            ErrorMessage = null;
            if (register == JbdFrame.RegBalanceControl && _balanceOn != _pendingBalanceRequest)
            {
                _balanceOn = _pendingBalanceRequest;
                RaisePropertyChanged(nameof(BalanceOn));
            }

            // 0xE1 受理只代表命令被接受，MOS 实际状态仍等下一轮 0x03 回读刷新
        });
    }

    private void OnResponseTimedOut()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (IsConnected)
            {
                ConnectionState = ConnectionState.Timeout;
            }
        });
    }

    /// <summary>串口线程 → Dispatcher marshal 回 UI 线程后才碰绑定属性（硬约束 4.3）。</summary>
    private void OnBasicInfoReceived(BasicInfo info)
    {
        _dispatcher.BeginInvoke(() =>
        {
            TotalVoltageV = info.TotalVoltageV;
            CurrentA = info.CurrentA;
            SocPercent = info.SocPercent;
            UpdateMosFromReadback(info);
            UpdateProtections(info);
            LastUpdated = DateTime.Now;
            if (IsConnected)
            {
                ConnectionState = ConnectionState.Communicating;
            }
        });
    }

    /// <summary>MOS 开关显示值只从 0x03 回读更新（绕过公开 setter 的写副作用）。UI 线程调用。</summary>
    private void UpdateMosFromReadback(BasicInfo info)
    {
        if (_chargeMosOn != info.ChargeMosOn)
        {
            _chargeMosOn = info.ChargeMosOn;
            RaisePropertyChanged(nameof(ChargeMosOn));
        }

        if (_dischargeMosOn != info.DischargeMosOn)
        {
            _dischargeMosOn = info.DischargeMosOn;
            RaisePropertyChanged(nameof(DischargeMosOn));
        }
    }

    /// <summary>保护面板与锁定标识只从 0x03 回读更新。UI 线程调用。</summary>
    private void UpdateProtections(BasicInfo info)
    {
        var labels = JbdProtectionStatus.GetActiveLabels(info.ProtectionStatus);
        if (!labels.SequenceEqual(ActiveProtections))
        {
            ActiveProtections.Clear();
            foreach (string label in labels)
            {
                ActiveProtections.Add(label);
            }

            RaisePropertyChanged(nameof(HasNoProtections));
        }

        IsMosLocked = info.MosSoftwareLocked;
    }

    private void OnCellVoltagesReceived(CellVoltages cells)
    {
        _dispatcher.BeginInvoke(() =>
        {
            while (Cells.Count < cells.CellVoltagesV.Count)
            {
                Cells.Add(new CellReading(Cells.Count + 1));
            }

            while (Cells.Count > cells.CellVoltagesV.Count)
            {
                Cells.RemoveAt(Cells.Count - 1);
            }

            for (int i = 0; i < cells.CellVoltagesV.Count; i++)
            {
                Cells[i].VoltageV = cells.CellVoltagesV[i];
            }

            LastUpdated = DateTime.Now;
        });
    }
}

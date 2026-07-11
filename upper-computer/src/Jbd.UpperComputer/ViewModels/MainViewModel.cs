using System.Collections.ObjectModel;
using System.IO.Ports;
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

public class MainViewModel : BindableBase
{
    private string? _selectedPort;
    private int _baudRate = 9600;
    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private double _totalVoltageV;
    private double _currentA;
    private int _socPercent;
    private DateTime? _lastUpdated;

    public MainViewModel()
    {
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
        // 壳子：真正的串口打开与轮询在后续提交接入
        ConnectionState = ConnectionState.Connected;
    }

    private void Disconnect()
    {
        ConnectionState = ConnectionState.Disconnected;
    }
}

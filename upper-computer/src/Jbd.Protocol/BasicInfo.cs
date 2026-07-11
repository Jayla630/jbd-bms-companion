namespace Jbd.Protocol;

/// <summary>
/// 0x03 基础信息（已换算成工程单位）。本切片界面只展示前三项，其余字段按范围约定暂不解析。
/// </summary>
public sealed record BasicInfo(double TotalVoltageV, double CurrentA, int SocPercent);

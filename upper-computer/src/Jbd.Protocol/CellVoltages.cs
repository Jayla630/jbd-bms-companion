namespace Jbd.Protocol;

/// <summary>0x04 单体电压（已换算成 V，本板 4 串）。</summary>
public sealed record CellVoltages(IReadOnlyList<double> CellVoltagesV);

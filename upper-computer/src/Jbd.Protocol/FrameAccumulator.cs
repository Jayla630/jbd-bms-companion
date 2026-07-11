namespace Jbd.Protocol;

/// <summary>
/// 串口字节流帧累积器：串口一次给的数据未必是整帧（分包），也可能不止一帧（粘包）。
/// 喂入任意字节块，切出所有完整帧（DD … 77，总长 = 长度字段 + 7）；
/// 遇到假起始码、结尾不对或长度离谱时丢弃重新对齐，不会卡死缓冲。
/// 非线程安全，调用方保证只在一个线程上喂数据（串口接收线程）。
/// </summary>
public sealed class FrameAccumulator
{
    /// <summary>数据区长度上限。协议数据区最长不过几十字节，超过视为错位垃圾。</summary>
    public const int MaxDataLength = 64;

    private readonly List<byte> _buffer = [];

    /// <summary>喂入一块数据，返回本次切出的完整帧（可能为空）。</summary>
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> chunk)
    {
        foreach (byte b in chunk)
        {
            _buffer.Add(b);
        }

        var frames = new List<byte[]>();
        while (true)
        {
            int start = _buffer.IndexOf(JbdFrame.Start);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }

            _buffer.RemoveRange(0, start);
            if (_buffer.Count < 4)
            {
                break; // 长度字段还没到，等下一块
            }

            int dataLength = _buffer[3];
            if (dataLength > MaxDataLength)
            {
                _buffer.RemoveAt(0); // 长度离谱：假起始码，丢一个字节重新找
                continue;
            }

            int total = dataLength + JbdFrame.ResponseOverhead;
            if (_buffer.Count < total)
            {
                break; // 帧还没攒够
            }

            if (_buffer[total - 1] != JbdFrame.End)
            {
                _buffer.RemoveAt(0); // 结尾不对：假帧，丢起始码重新对齐
                continue;
            }

            frames.Add([.. _buffer.Take(total)]);
            _buffer.RemoveRange(0, total);
        }

        return frames;
    }

    public void Clear() => _buffer.Clear();
}

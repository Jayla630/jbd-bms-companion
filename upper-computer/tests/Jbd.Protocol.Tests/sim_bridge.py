"""stdio 桥：把 Python 模拟器的 Device.handle_request 暴露成"hex 行进、hex 行出"，
供 C# 集成测试（SimulatorIntegrationTests）脱离虚拟串口直接对真模拟器跑第 1 层验证。

用法：python sim_bridge.py <simulator目录路径>
协议：每行一个请求帧的 hex；对每行输出一行响应帧 hex（空行 = 无响应）；"quit" 退出。
只 import 模拟器现有模块，不改模拟器任何代码。
"""
import sys

sys.path.insert(0, sys.argv[1])

from bms_sim import protocol as proto  # noqa: E402
from bms_sim.device import Device  # noqa: E402


def main() -> None:
    device = Device()
    decoder = proto.FrameDecoder("request")
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        if line == "quit":
            return
        frames = decoder.feed(bytes.fromhex(line))
        response = b"".join(device.handle_request(frame) for frame in frames)
        print(response.hex(), flush=True)


if __name__ == "__main__":
    main()

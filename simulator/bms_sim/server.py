"""真实串口收发主循环。process_available()/serve() 只依赖 port 对象提供
read(n)/write(data) 两个方法（serial.Serial 满足这个接口，测试里用假对象替代）。
"""
from __future__ import annotations

import argparse
import time

import serial

from . import protocol as proto
from .device import Device


def process_available(port, device: Device, decoder: proto.FrameDecoder) -> int:
    """读一次串口缓冲区、处理其中的完整请求帧并写回响应，返回处理的帧数。"""
    chunk = port.read(64)
    if not chunk:
        return 0
    frames = decoder.feed(chunk)
    for frame in frames:
        port.write(device.handle_request(frame))
    return len(frames)


def serve(port, device: Device | None = None) -> None:
    device = device or Device()
    decoder = proto.FrameDecoder("request")
    print("BMS 模拟器已启动，按 Ctrl+C 退出")
    try:
        while True:
            if process_available(port, device, decoder) == 0:
                time.sleep(0.01)
    except KeyboardInterrupt:
        print("已停止")


def main() -> None:
    parser = argparse.ArgumentParser(description="JBD SP04S010 串口 BMS 模拟器")
    parser.add_argument("--port", required=True, help="串口号，例如 COM6 或 /dev/pts/3")
    parser.add_argument("--baudrate", type=int, default=9600)
    args = parser.parse_args()
    with serial.Serial(args.port, baudrate=args.baudrate, timeout=0.05) as ser:
        serve(ser)


if __name__ == "__main__":
    main()

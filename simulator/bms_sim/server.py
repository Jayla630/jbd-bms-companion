"""真实串口收发主循环。process_available()/serve() 只依赖 port 对象提供
read(n)/write(data) 两个方法（serial.Serial 满足这个接口，测试里用假对象替代）。

交互式控制台：serve 主线程跑串口收发的同时，可起一个 daemon 线程读 stdin，
命令经 cli.handle_command 作用在【与串口收发共享的同一个 Device】上——
在 server 控制台里改电流/SOC/注入故障，串口对端的上位机能当场看到数值变化。
"""
from __future__ import annotations

import argparse
import sys
import threading
import time

import serial

from . import cli
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


def console_loop(device: Device, stream=None) -> None:
    """控制台命令循环：逐行读入并作用到共享 Device。EOF（stdin 被关闭/管道结束）
    或 quit 命令时安静退出，不影响串口主循环。"""
    stream = stream if stream is not None else sys.stdin
    for line in stream:
        stripped = line.strip()
        if not stripped:
            continue
        if stripped == "help":
            print(
                "命令: current <mA> | soc <percent> | fault inject/clear <name> | "
                "mos <hex> | temp <celsius> | status | quit(仅退出控制台，Ctrl+C 停 server)"
            )
            continue
        try:
            if not cli.handle_command(device, stripped):
                print("控制台已退出（串口服务继续运行，Ctrl+C 停止 server）")
                return
        except Exception as exc:  # 控制台输错命令不能拖垮串口服务
            print(f"命令执行失败: {exc}")


def serve(port, device: Device | None = None, interactive: bool = False) -> None:
    device = device or Device()
    decoder = proto.FrameDecoder("request")
    print("BMS 模拟器已启动，按 Ctrl+C 退出")
    if interactive:
        print("交互控制台已开启，输入 help 查看命令（作用于串口同一台模拟器）")
        threading.Thread(target=console_loop, args=(device,), daemon=True).start()
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
    parser.add_argument(
        "--interactive",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="是否开启 stdin 交互控制台（默认开；纯自动化场景用 --no-interactive 关闭）",
    )
    args = parser.parse_args()
    with serial.Serial(args.port, baudrate=args.baudrate, timeout=0.05) as ser:
        serve(ser, interactive=args.interactive)


if __name__ == "__main__":
    main()

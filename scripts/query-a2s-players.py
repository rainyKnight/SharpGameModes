#!/usr/bin/env python3

import argparse
import socket
import struct


def read_c_string(data: bytes, offset: int) -> tuple[str, int]:
    end = data.index(b"\0", offset)
    return data[offset:end].decode("utf-8", "replace"), end + 1


def main() -> None:
    parser = argparse.ArgumentParser(description="Query Source A2S_PLAYER data.")
    parser.add_argument("host")
    parser.add_argument("port", type=int)
    parser.add_argument("--timeout", type=float, default=3.0)
    args = parser.parse_args()

    target = (args.host, args.port)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(args.timeout)
        sock.sendto(b"\xff\xff\xff\xffU\xff\xff\xff\xff", target)
        response = sock.recvfrom(4096)[0]

        if response[:5] == b"\xff\xff\xff\xffA":
            sock.sendto(b"\xff\xff\xff\xffU" + response[5:9], target)
            response = sock.recvfrom(65535)[0]

    if response[:5] != b"\xff\xff\xff\xffD":
        raise SystemExit(f"Unexpected A2S_PLAYER response: {response[:16]!r}")

    count = response[5]
    offset = 6
    print(f"A2S_PLAYER_COUNT={count}")

    for _ in range(count):
        index = response[offset]
        offset += 1
        name, offset = read_c_string(response, offset)
        score, duration = struct.unpack_from("<if", response, offset)
        offset += 8
        print(f"{index}\t{name}\t{score}\t{duration:.1f}")


if __name__ == "__main__":
    main()

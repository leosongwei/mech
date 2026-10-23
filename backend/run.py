"""Dev entry point: python run.py [--host 0.0.0.0] [--port 8000] [--range]"""
import argparse
import os

import uvicorn

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8000)
    ap.add_argument("--range", action="store_true",
                    help="weapon range mode: 4 km test map with APC targets")
    args = ap.parse_args()
    if args.range:
        # the world is built at import time, so set this before uvicorn loads it
        os.environ["MECH_RANGE"] = "1"
    uvicorn.run("server.main:app", host=args.host, port=args.port, log_level="info")

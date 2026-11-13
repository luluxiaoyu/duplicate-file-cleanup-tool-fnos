#!/usr/bin/env python3
"""Allow native runtime graph differences, never drift in the common managed dependency lock."""
import json
import sys


def verify(reference, actual):
    expected = reference["dependencies"]["net10.0"]
    restored = actual["dependencies"]["net10.0"]
    if set(expected) != set(restored):
        raise ValueError("Common NuGet package set changed; update and review packages.lock.json")
    for name, package in expected.items():
        for field in ("resolved", "contentHash"):
            if restored[name].get(field) != package.get(field):
                raise ValueError(f"Common NuGet package changed: {name} ({field})")
    compiler_version = expected["Microsoft.DotNet.ILCompiler"]["resolved"]
    for graph in actual["dependencies"].values():
        for name, package in graph.items():
            if name.startswith("runtime.") and name.endswith(".Microsoft.DotNet.ILCompiler"):
                if package["resolved"] != compiler_version:
                    raise ValueError("AOT compiler/runtime versions do not match")


if __name__ == "__main__":
    with open(sys.argv[1], encoding="utf-8") as stream:
        reference = json.load(stream)
    with open(sys.argv[2], encoding="utf-8") as stream:
        actual = json.load(stream)
    verify(reference, actual)
    print("Common NuGet versions and content hashes match; platform runtime graph verified")

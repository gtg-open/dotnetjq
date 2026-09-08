#!/usr/bin/env python3
"""Create a native-path C# PathMap value escaped for one MSBuild property."""

import os
import sys


def build_map(pairs, path_module=os.path):
    entries = []
    for source, destination in pairs:
        # macOS TMPDIR ends in '/', while Git Bash may use short or POSIX
        # Windows paths. Python receives native argv on Windows. Match both
        # the lexical absolute path and its resolved physical/long-name alias.
        prefixes = dict.fromkeys((path_module.abspath(source), path_module.realpath(source)))
        for prefix in prefixes:
            prefix = prefix.rstrip('/\\') + path_module.sep
            compiler_key = prefix.replace('=', '==').replace(',', ',,')
            entries.append(f'{compiler_key}={destination}')
    # Escape MSBuild property separators only after escaping C# PathMap keys.
    return ','.join(entries).replace('%', '%25').replace(',', '%2C').replace(';', '%3B')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        raise SystemExit('usage: compiler-path-map.py REPOSITORY_ROOT BUILD_ROOT')
    print(build_map(((sys.argv[1], '/_/'), (sys.argv[2], '/_build/'))))

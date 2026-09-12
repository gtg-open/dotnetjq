#!/usr/bin/env python3
"""Regression tests for native compiler path spelling and property escaping."""

import importlib.util
import ntpath
import posixpath
from pathlib import Path
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    'compiler_path_map', Path(__file__).with_name('compiler-path-map.py')
)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)

# Test lexical spelling independently of the host filesystem. In particular,
# real macOS /tmp is an alias of /private/tmp, not an alias-free fixture.
with patch.object(posixpath, 'realpath', side_effect=posixpath.abspath), \
     patch.object(ntpath, 'realpath', side_effect=ntpath.abspath):
    assert MODULE.build_map((('/tmp/build//first/', '/_build/'),), posixpath) == '/tmp/build/first/=/_build/'
    assert MODULE.build_map(((r'C:\build-fixture\Temp\first', '/_build/'),), ntpath) == 'C:\\build-fixture\\Temp\\first\\=/_build/'
    assert MODULE.build_map((('C:/build-fixture/Temp//first/', '/_build/'),), ntpath) == 'C:\\build-fixture\\Temp\\first\\=/_build/'
    assert MODULE.build_map((('/tmp/a,b=c;%d', '/_build/'),), posixpath) == '/tmp/a%2C%2Cb==c%3B%25d/=/_build/'
    assert MODULE.build_map((('/repo', '/_/'), ('/build', '/_build/')), posixpath) == '/repo/=/_/%2C/build/=/_build/'

# Require both aliases, rather than dropping physical-path coverage to make
# the macOS assertion pass. These cases run on every host, including Linux.
with patch.object(posixpath, 'realpath', return_value='/private/tmp/build/first'):
    assert MODULE.build_map((('/tmp/build//first/', '/_build/'),), posixpath) == (
        '/tmp/build/first/=/_build/%2C/private/tmp/build/first/=/_build/'
    )
with patch.object(ntpath, 'realpath', return_value=r'C:\build-fixture\long directory\Temp\first'):
    assert MODULE.build_map(((r'C:\build-fixture\LONGDI~1\Temp\first', '/_build/'),), ntpath) == (
        'C:\\build-fixture\\LONGDI~1\\Temp\\first\\=/_build/%2CC:\\build-fixture\\long directory\\Temp\\first\\=/_build/'
    )
print('compiler path-map regression tests passed (7/7)')

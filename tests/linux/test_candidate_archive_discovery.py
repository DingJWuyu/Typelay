"""Exercise archive-cache isolation in the #80 merge candidate replay."""

import tempfile
import unittest
from pathlib import Path

from eng.linux.cooperative.archives import validate_cache


class ArchivedSourceTests(unittest.TestCase):
    def test_separate_cache_and_build_directories(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cache = root / "cache"
            self.assertEqual(validate_cache(cache, root / "build"), cache.resolve())

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from cart import total


class HiddenCartTests(unittest.TestCase):
    def test_empty_cart(self):
        self.assertEqual(total([]), 0)

    def test_decimal_prices(self):
        self.assertEqual(total([1.25, 2.75]), 4.0)


if __name__ == "__main__":
    unittest.main()

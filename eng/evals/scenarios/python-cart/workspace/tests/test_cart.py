import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from cart import total


class CartTests(unittest.TestCase):
    def test_sums_prices_without_adding_item_count(self):
        self.assertEqual(total([2, 3]), 5)


if __name__ == "__main__":
    unittest.main()

import test from "node:test";
import assert from "node:assert/strict";
import { discounted } from "../src/discount.js";

test("applies a percentage rather than subtracting currency", () => {
  assert.equal(discounted(200, 10), 180);
});

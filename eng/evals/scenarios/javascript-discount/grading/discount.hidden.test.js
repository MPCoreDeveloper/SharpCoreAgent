import test from "node:test";
import assert from "node:assert/strict";
import { discounted } from "../src/discount.js";

test("zero percent leaves the price unchanged", () => {
  assert.equal(discounted(80, 0), 80);
});

test("one hundred percent makes the price zero", () => {
  assert.equal(discounted(80, 100), 0);
});

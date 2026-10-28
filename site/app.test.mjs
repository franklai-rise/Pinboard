import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { test } from "node:test";
import assert from "node:assert/strict";

// Exercise the same pure helper used by the published browser entrypoint.
const source = readFileSync(new URL("./app.js", import.meta.url), "utf8");
const helper = source.slice(source.indexOf("function demoSearchSnippet("), source.indexOf("// END SEARCH HELPERS"));
const snippet = runInNewContext(`${helper}\ndemoSearchSnippet`);

test("search centers a late keyword with nearby context instead of the beginning of a clip", () => {
  const result = snippet(`${"Earlier material. ".repeat(80)}The reference keyword matters here. Later context.`, "KEYWORD", 25);
  assert.equal(result.match, "keyword");
  assert.ok(result.before.startsWith("…"));
  assert.ok(result.before.includes("reference"));
  assert.ok(result.after.includes("matters here"));
  assert.ok(result.before.length < 30);
});

test("Chinese and multiline phrases return a highlightable match", () => {
  const result = snippet("收集新的想法，\n保留参考\n资料以便查找。", "参考 资料");
  assert.equal(result.match, "参考 资料");
  assert.equal(result.score, 2);
  assert.equal(snippet("中文截图与文字剪藏", "缺失 文字").match, "文字");
});

test("empty and absent queries produce no match; markup remains plain text", () => {
  assert.equal(snippet("example", " "), undefined);
  assert.equal(snippet("example", "absent"), undefined);
  const result = snippet("Keep <script>idea</script> as plain text", "idea");
  assert.ok(result.before.includes("<script>"));
  assert.ok(result.after.includes("</script>"));
});

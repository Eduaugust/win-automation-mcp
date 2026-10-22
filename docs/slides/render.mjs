// Renders the talk slides in this folder to docs/img/slide-<id>.png (1920x1080 canvas).
//   PLAYWRIGHT=/path/to/node_modules/playwright node docs/slides/render.mjs
import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT ?? "playwright");
const here = dirname(fileURLToPath(import.meta.url));

const head = `<!doctype html><html><head>
<link href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400..700&family=JetBrains+Mono:wght@400;600&display=swap" rel="stylesheet">
<style>
* { margin: 0; box-sizing: border-box; }
body { width: 1920px; height: 1080px; overflow: hidden; }
section { position: relative; width: 1920px; height: 1080px; }
aside { display: none; }
ul, ol { padding-left: 40px; }
table { border-collapse: collapse; width: 100%; }
th, td { padding: 24px 28px; text-align: left; border-bottom: 1px solid #d9d2c3; }
</style></head><body>`;

const browser = await chromium.launch({ channel: process.env.CHANNEL ?? "chrome" });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
for (const file of readdirSync(here).filter((f) => f.endsWith(".html"))) {
  await page.setContent(head + readFileSync(join(here, file), "utf8") + "</body></html>", { waitUntil: "networkidle" });
  await page.evaluate(() => document.fonts.ready);
  const out = join(here, "..", "img", `slide-${file.replace(".html", "")}.png`);
  await page.screenshot({ path: out });
  console.log(out);
}
await browser.close();

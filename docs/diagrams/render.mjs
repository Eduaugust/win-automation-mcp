// Renders docs/diagrams/scenes.mjs with Excalidraw in headless Chromium.
//   PLAYWRIGHT=/path/to/node_modules/playwright node docs/diagrams/render.mjs
import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";
import { scenes } from "./scenes.mjs";

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT ?? "playwright");

const here = dirname(fileURLToPath(import.meta.url));
const img = join(here, "..", "img");
const X = "https://unpkg.com/@excalidraw/excalidraw@0.17.6/dist/";

const html = `<!doctype html><html><head>
<script>window.EXCALIDRAW_ASSET_PATH = "${X}";</script>
<script src="https://unpkg.com/react@18.2.0/umd/react.production.min.js"></script>
<script src="https://unpkg.com/react-dom@18.2.0/umd/react-dom.production.min.js"></script>
<script src="${X}excalidraw.production.min.js"></script>
</head><body style="margin:0;background:#fff"><div id="out" style="display:inline-block"></div></body></html>`;

const browser = await chromium.launch({ channel: process.env.CHANNEL ?? "chrome" });
const page = await browser.newPage({ deviceScaleFactor: 1.5 });
await page.setContent(html, { waitUntil: "networkidle" });

for (const [name, skeleton] of Object.entries(scenes)) {
  const elements = await page.evaluate(async (skeleton) => {
    const { convertToExcalidrawElements, exportToSvg } = window.ExcalidrawLib;
    const elements = convertToExcalidrawElements(skeleton, { regenerateIds: false });
    const svg = await exportToSvg({
      elements,
      appState: { exportBackground: true, viewBackgroundColor: "#ffffff", exportPadding: 32 },
      files: null,
    });
    const out = document.getElementById("out");
    out.replaceChildren(svg);
    await document.fonts.ready;
    return elements;
  }, skeleton);
  await page.waitForTimeout(300);
  writeFileSync(join(here, `${name}.excalidraw`), JSON.stringify({
    type: "excalidraw", version: 2, source: "https://excalidraw.com",
    elements, appState: { viewBackgroundColor: "#ffffff" }, files: {},
  }, null, 2));
  await page.locator("#out svg").screenshot({ path: join(img, `${name}.png`) });
  console.log(`${name}: ${elements.length} elements`);
}

await browser.close();

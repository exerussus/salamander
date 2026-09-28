// Рендер мастер-SVG в PNG через Chromium (Playwright): тот же движок, что
// рисует SVG в браузерах, так что сглаживание совпадает с тем, что видно в
// предпросмотре. Вызывается из build.py: node render.js <папка вывода>.
// 16 px берётся из упрощённой версии, остальные — из полной.
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const out = process.argv[2] || path.join(__dirname, 'png');
const big = fs.readFileSync(path.join(__dirname, 'salamander.svg'), 'utf8');
const small = fs.readFileSync(path.join(__dirname, 'salamander-small.svg'), 'utf8');

(async () => {
    const browser = await chromium.launch();
    // окно с запасом: у Chromium есть минимальная ширина, а мы снимаем
    // элемент нужного размера, а не всё окно
    const page = await browser.newPage({ viewport: { width: 400, height: 400 } });
    for (const size of [16, 32, 48, 64, 128]) {
        const svg = (size <= 16 ? small : big).replace(/width="\d+" height="\d+"/, `width="${size}" height="${size}"`);
        await page.setContent(
            `<html><body style="margin:0;background:transparent">` +
            `<div id="c" style="width:${size}px;height:${size}px;line-height:0">${svg}</div></body></html>`);
        await (await page.$('#c')).screenshot({ path: path.join(out, `salamander-${size}.png`), omitBackground: true });
    }
    await browser.close();
})();

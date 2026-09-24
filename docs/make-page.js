/**
 * 把 README.md 转成 docs/index.html（GitHub Pages 用）。
 * 改完 README 后在项目根目录执行：node docs/make-page.js
 */

const fs = require('fs');
const path = require('path');

const docsDir = __dirname;
const rootDir = path.dirname(docsDir);
const repo = 'https://github.com/DingYaoYao/sms-to-pc';

const markdown = fs.readFileSync(path.join(rootDir, 'README.md'), 'utf8').replace(/\r\n/g, '\n');
const lines = markdown.split('\n');

const escapeHtml = (text) =>
    text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** 页面放在 docs/ 下，所以 docs/xxx.png 要变成 ./xxx.png；仓库内其它链接指回 GitHub。 */
function rewriteUrl(url) {
    if (/^https?:/i.test(url)) return url;
    if (url.startsWith('../../releases')) return repo + '/releases';
    if (url.startsWith('docs/')) return url.slice(5);
    if (url.startsWith('dist/')) return repo + '/blob/main/' + encodeURI(url);
    return url;
}

/** 行内格式：自动链接、图片、链接、行内代码、粗体。 */
function inline(text) {
    const autolinks = [];
    const images = [];
    const links = [];

    let out = text.replace(/<(https?:\/\/[^>\s]+)>/g, (_, url) => {
        autolinks.push(url);
        return `\u0000A${autolinks.length - 1}\u0000`;
    });

    out = escapeHtml(out);

    out = out.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, (_, alt, src) => {
        images.push({ alt, src: rewriteUrl(src) });
        return `\u0000I${images.length - 1}\u0000`;
    });

    out = out.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_, label, href) => {
        links.push({ label, href: rewriteUrl(href) });
        return `\u0000L${links.length - 1}\u0000`;
    });

    out = out
        .replace(/`([^`]+)`/g, '<code>$1</code>')
        .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

    out = out.replace(/\u0000A(\d+)\u0000/g, (_, i) =>
        `<a href="${autolinks[i]}" target="_blank" rel="noopener">${autolinks[i]}</a>`);

    out = out.replace(/\u0000I(\d+)\u0000/g, (_, i) =>
        `<img src="${images[i].src}" alt="${images[i].alt}" loading="lazy">`);

    out = out.replace(/\u0000L(\d+)\u0000/g, (_, i) => {
        const { label, href } = links[i];
        const text = label
            .replace(/`([^`]+)`/g, '<code>$1</code>')
            .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
        const target = /^https?:/i.test(href) ? ' target="_blank" rel="noopener"' : '';
        return `<a href="${href}"${target}>${text}</a>`;
    });

    return out;
}

const isTableDivider = (line) => /^\|[\s:|-]+\|$/.test(line.trim());
const isRawHtml = (line) =>
    /^\s*<\/?[a-zA-Z][^>]*>\s*$/.test(line) ||
    /^\s*<[a-zA-Z][^>]*>.*<\/[a-zA-Z]+>\s*$/.test(line);

const html = [];
let paragraph = [];
let listItems = [];
let listTag = '';
let listClass = '';
let tableRows = [];
let codeBlock = null;

function flushParagraph() {
    if (!paragraph.length) return;
    html.push(`<p>${paragraph.map((line) => inline(line)).join('<br>')}</p>`);
    paragraph = [];
}

function flushList() {
    if (!listItems.length) return;
    const cls = listClass ? ` class="${listClass}"` : '';
    html.push(`<${listTag}${cls}>${listItems.map((item) => `<li>${item}</li>`).join('')}</${listTag}>`);
    listItems = [];
    listTag = '';
    listClass = '';
}

function flushTable() {
    if (!tableRows.length) return;
    const rows = tableRows.filter((row) => !isTableDivider(row)).map((row) =>
        row.replace(/^\s*\|/, '').replace(/\|\s*$/, '').split('|').map((cell) => cell.trim()));
    const [head, ...body] = rows;
    const headHtml = head.map((cell) => `<th>${inline(cell)}</th>`).join('');
    const bodyHtml = body.map((row) =>
        `<tr>${row.map((cell) => `<td>${inline(cell)}</td>`).join('')}</tr>`).join('');
    html.push(`<div class="table-wrap"><table><thead><tr>${headHtml}</tr></thead><tbody>${bodyHtml}</tbody></table></div>`);
    tableRows = [];
}

for (const rawLine of lines) {
    const line = rawLine.replace(/\s+$/, '');

    if (codeBlock !== null) {
        if (line.trim().startsWith('```')) {
            html.push(`<pre><code>${escapeHtml(codeBlock.join('\n'))}</code></pre>`);
            codeBlock = null;
        } else {
            codeBlock.push(rawLine);
        }
        continue;
    }

    if (line.trim().startsWith('```')) {
        flushParagraph(); flushList(); flushTable();
        codeBlock = [];
        continue;
    }

    if (!line.trim()) {
        flushParagraph(); flushList(); flushTable();
        continue;
    }

    if (/^-{3,}$/.test(line.trim())) {
        flushParagraph(); flushList(); flushTable();
        html.push('<hr>');
        continue;
    }

    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
        flushParagraph(); flushList(); flushTable();
        const level = heading[1].length;
        const text = inline(heading[2]);
        html.push(`<h${level}>${text}</h${level}>`);
        continue;
    }

    if (line.trim().startsWith('|')) {
        flushParagraph(); flushList();
        tableRows.push(line.trim());
        continue;
    }

    if (/^>\s?/.test(line)) {
        flushParagraph(); flushList(); flushTable();
        html.push(`<blockquote>${inline(line.replace(/^>\s?/, ''))}</blockquote>`);
        continue;
    }

    // 独占一行的图片，单独成块（避免两张图挤在同一段里）
    if (/^!\[[^\]]*\]\([^)]+\)$/.test(line.trim())) {
        flushParagraph(); flushList(); flushTable();
        html.push(`<p class="shot">${inline(line.trim())}</p>`);
        continue;
    }

    const bullet = /^([ \t]*)[-*]\s+(.*)$/.exec(line);
    if (bullet) {
        flushParagraph(); flushTable();
        const nested = bullet[1].length > 0;
        if (listTag && (listTag !== 'ul' || Boolean(listClass) !== nested)) flushList();
        listTag = 'ul';
        listClass = nested ? 'sublist' : '';
        listItems.push(inline(bullet[2]));
        continue;
    }

    const ordered = /^\d+\.\s+(.*)$/.exec(line);
    if (ordered) {
        flushParagraph(); flushTable();
        if (listTag && (listTag !== 'ol' || listClass)) flushList();
        listTag = 'ol';
        listClass = '';
        listItems.push(inline(ordered[1]));
        continue;
    }

    if (isRawHtml(line)) {
        flushParagraph(); flushList(); flushTable();
        html.push(line.replace(/src="docs\//g, 'src="'));
        continue;
    }

    flushList(); flushTable();
    paragraph.push(line);
}

flushParagraph(); flushList(); flushTable();
if (codeBlock !== null) html.push(`<pre><code>${escapeHtml(codeBlock.join('\n'))}</code></pre>`);

const page = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>码上来 · 手机短信和验证码自动同步到电脑</title>
<meta name="description" content="手机收到的短信和验证码，立刻出现在电脑上。局域网加密直连，不经过任何服务器。">
<style>
:root {
  --brand: #4F46E5;
  --brand-soft: #EEF0FE;
  --bg: #FFFFFF;
  --fg: #1C1F26;
  --muted: #6B7280;
  --line: #E6E8EF;
  --soft: #F7F8FB;
  --code-bg: #1C1F26;
  --code-fg: #E6E8EF;
}
@media (prefers-color-scheme: dark) {
  :root {
    --brand: #8B83F0;
    --brand-soft: #232544;
    --bg: #0E1014;
    --fg: #E7E9EE;
    --muted: #9AA1AC;
    --line: #272B34;
    --soft: #16191F;
    --code-bg: #05070A;
    --code-fg: #D7DAE0;
  }
}
* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body {
  margin: 0;
  background: var(--bg);
  color: var(--fg);
  font: 16px/1.8 "PingFang SC", "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", system-ui, sans-serif;
  word-break: break-word;
}
main { max-width: 880px; margin: 0 auto; padding: 56px 22px 96px; }
h1 { font-size: 38px; line-height: 1.25; margin: 0 0 12px; letter-spacing: .5px; }
h2 { font-size: 24px; margin: 56px 0 16px; padding-top: 8px; }
h3 { font-size: 18px; margin: 34px 0 12px; color: var(--brand); }
p { margin: 14px 0; color: var(--fg); }
strong { font-weight: 600; }
a { color: var(--brand); text-decoration: none; border-bottom: 1px solid transparent; }
a:hover { border-bottom-color: currentColor; }
hr { border: 0; border-top: 1px solid var(--line); margin: 40px 0; }
img {
  display: block;
  max-width: 100%;
  max-height: 620px;
  width: auto;
  height: auto;
  margin: 22px auto;
  border: 1px solid var(--line);
  border-radius: 14px;
  box-shadow: 0 6px 24px rgba(16, 24, 40, .08);
}
code {
  font-family: Consolas, "SFMono-Regular", Menlo, monospace;
  font-size: .92em;
  background: var(--soft);
  border: 1px solid var(--line);
  border-radius: 6px;
  padding: 1px 6px;
}
pre {
  background: var(--code-bg);
  color: var(--code-fg);
  border-radius: 12px;
  padding: 18px 20px;
  overflow-x: auto;
}
pre code { background: none; border: 0; padding: 0; color: inherit; font-size: 13.5px; line-height: 1.7; }
ul, ol { padding-left: 26px; }
li { margin: 6px 0; }
ul.sublist { margin: 6px 0 10px; padding-left: 26px; }
ul.sublist li { color: var(--muted); }
p.shot { margin: 22px 0; }
blockquote {
  margin: 18px 0;
  padding: 14px 18px;
  background: var(--soft);
  border-left: 4px solid var(--brand);
  border-radius: 0 10px 10px 0;
  color: var(--muted);
}
blockquote p { margin: 0; color: inherit; }
.table-wrap { overflow-x: auto; margin: 20px 0; }
table { border-collapse: collapse; width: 100%; font-size: 15px; }
th, td { border: 1px solid var(--line); padding: 10px 14px; text-align: left; vertical-align: top; }
th { background: var(--brand-soft); font-weight: 600; white-space: nowrap; }
tbody tr:nth-child(even) { background: var(--soft); }
@media (max-width: 600px) {
  main { padding: 34px 16px 60px; }
  h1 { font-size: 30px; }
  h2 { font-size: 21px; margin-top: 42px; }
  img { max-height: 460px; }
}
</style>
</head>
<body>
<main>
${html.join('\n')}
</main>
</body>
</html>
`;

fs.writeFileSync(path.join(docsDir, 'index.html'), page, 'utf8');
console.log(`已生成 docs/index.html（${(Buffer.byteLength(page) / 1024).toFixed(1)} KB）`);

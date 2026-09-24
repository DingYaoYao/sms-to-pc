/**
 * 把 README.md 转成 docs/index.html（GitHub Pages 用）。
 * 改完 README 后在项目根目录执行：node docs/make-page.js
 */

const fs = require('fs');
const path = require('path');

const docsDir = __dirname;
const rootDir = path.dirname(docsDir);
const repo = 'https://github.com/DingYaoYao/sms-to-pc';
const releases = repo + '/releases';

const markdown = fs.readFileSync(path.join(rootDir, 'README.md'), 'utf8').replace(/\r\n/g, '\n');

// 第一个 --- 之前是首屏素材，其余是正文
const blocks = markdown.split(/\n-{3,}\n/);
const heroMarkdown = blocks[0];
const articleMarkdown = blocks.slice(1).join('\n---\n');

// 从 README 里抓蓝奏云链接，避免两处手工同步
// 可能是 [文字](url) 也可能是 <url> 自动链接，两种都认
const lanzouMatch = /[\(<](https?:\/\/[\w.-]*lanzoue\.com\/[^)>\s]+)/.exec(markdown);
const lanzouUrl = lanzouMatch ? lanzouMatch[1] : '';
// 顺带把分享密码也抓出来（README 里写作：密码：`2dq1`）
const lanzouPasswordMatch = /密码[:：]\s*`?([A-Za-z0-9]{2,12})`?/.exec(markdown);
const lanzouPassword = lanzouPasswordMatch ? lanzouPasswordMatch[1] : '';

const escapeHtml = (text) =>
    text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** 页面放在 docs/ 下，所以 docs/xxx.png 要变成 ./xxx.png；仓库内其它链接指回 GitHub。 */
function rewriteUrl(url) {
    if (/^https?:/i.test(url)) return url;
    if (url.startsWith('../../releases')) return releases;
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

// 给几个主要章节加上锚点，导航栏要跳过去
const anchors = {
    '特性': 'features',
    '下载': 'download',
    '使用教程': 'guide',
    '常见问题': 'faq',
    '安全说明': 'security',
    '工作原理': 'how',
};

function convert(md) {
    const out = [];
    let paragraph = [];
    let listItems = [];
    let listTag = '';
    let listClass = '';
    let tableRows = [];
    let codeBlock = null;

    const flushParagraph = () => {
        if (!paragraph.length) return;
        out.push(`<p>${paragraph.map((line) => inline(line)).join('<br>')}</p>`);
        paragraph = [];
    };
    const flushList = () => {
        if (!listItems.length) return;
        const cls = listClass ? ` class="${listClass}"` : '';
        out.push(`<${listTag}${cls}>${listItems.map((item) => `<li>${item}</li>`).join('')}</${listTag}>`);
        listItems = [];
        listTag = '';
        listClass = '';
    };
    const flushTable = () => {
        if (!tableRows.length) return;
        const rows = tableRows
            .filter((row) => !isTableDivider(row))
            .map((row) => row.replace(/^\s*\|/, '').replace(/\|\s*$/, '').split('|').map((c) => c.trim()));
        const [head, ...body] = rows;
        const headHtml = head.map((cell) => `<th>${inline(cell)}</th>`).join('');
        const bodyHtml = body
            .map((row) => `<tr>${row.map((cell) => `<td>${inline(cell)}</td>`).join('')}</tr>`)
            .join('');
        out.push(`<div class="table-wrap"><table><thead><tr>${headHtml}</tr></thead><tbody>${bodyHtml}</tbody></table></div>`);
        tableRows = [];
    };

    for (const rawLine of md.split('\n')) {
        const line = rawLine.replace(/\s+$/, '');

        if (codeBlock !== null) {
            if (line.trim().startsWith('```')) {
                out.push(`<pre><code>${escapeHtml(codeBlock.join('\n'))}</code></pre>`);
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
            out.push('<hr>');
            continue;
        }

        const heading = /^(#{1,6})\s+(.*)$/.exec(line);
        if (heading) {
            flushParagraph(); flushList(); flushTable();
            const level = heading[1].length;
            const plain = heading[2].replace(/[*`]/g, '').trim();
            const id = anchors[plain] ? ` id="${anchors[plain]}"` : '';
            out.push(`<h${level}${id}>${inline(heading[2])}</h${level}>`);
            continue;
        }

        if (line.trim().startsWith('|')) {
            flushParagraph(); flushList();
            tableRows.push(line.trim());
            continue;
        }

        if (/^>\s?/.test(line)) {
            flushParagraph(); flushList(); flushTable();
            out.push(`<blockquote>${inline(line.replace(/^>\s?/, ''))}</blockquote>`);
            continue;
        }

        if (/^!\[[^\]]*\]\([^)]+\)$/.test(line.trim())) {
            flushParagraph(); flushList(); flushTable();
            out.push(`<figure>${inline(line.trim())}</figure>`);
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
            out.push(line.replace(/src="docs\//g, 'src="'));
            continue;
        }

        flushList(); flushTable();
        paragraph.push(line);
    }

    flushParagraph(); flushList(); flushTable();
    if (codeBlock !== null) out.push(`<pre><code>${escapeHtml(codeBlock.join('\n'))}</code></pre>`);
    return out.join('\n');
}

// ---- 首屏素材 ----
const heroLines = heroMarkdown.split('\n').map((line) => line.trim()).filter(Boolean);
const heroTitle = (heroLines.find((line) => line.startsWith('# ')) || '# 码上来').slice(2).trim();
const heroTagline = (heroLines.find((line) => line.startsWith('**')) || '').replace(/^\*\*|\*\*$/g, '');
const heroMeta = heroLines.find((line) =>
    !line.startsWith('#') && !line.startsWith('**') && !line.startsWith('![') &&
    !line.startsWith('仓库') && !line.startsWith('<')) || '';
// 联系方式直接从 README 里的邮箱生成，不依赖那一行的写法
const emailMatch = /邮箱[:：]\s*([^\s，,、]+)/.exec(markdown);
const email = emailMatch ? emailMatch[1] : '';
const heroContact =
    `开源仓库：<a href="${repo}" target="_blank" rel="noopener">${repo.replace('https://', '')}</a>` +
    (email ? ` ｜ 邮箱：${escapeHtml(email)}` : '');
const heroImageLine = heroLines.find((line) => line.startsWith('![')) || '';
const heroImage = (/!\[([^\]]*)\]\(([^)]+)\)/.exec(heroImageLine) || []).slice(1);

const article = convert(articleMarkdown);

const cta = [
    `<a class="btn primary" href="${releases}" target="_blank" rel="noopener">下载 Windows 版</a>`,
    `<a class="btn" href="${releases}" target="_blank" rel="noopener">下载安卓版</a>`,
    lanzouUrl ? `<a class="btn ghost" href="${lanzouUrl}" target="_blank" rel="noopener">蓝奏云（国内）</a>` : '',
].filter(Boolean).join('\n        ');

const lanzouNote = lanzouUrl && lanzouPassword
    ? `<p class="hint-line">蓝奏云下载密码：<code>${escapeHtml(lanzouPassword)}</code>
      <button class="copy" type="button" data-copy="${escapeHtml(lanzouPassword)}">复制</button></p>`
    : '';

const heroShot = heroImage.length === 2
    ? `<figure class="hero-shot"><img src="${rewriteUrl(heroImage[1])}" alt="${heroImage[0]}"></figure>`
    : '';

const page = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(heroTitle)} · 手机短信和验证码自动同步到电脑</title>
<meta name="description" content="手机收到的短信和验证码，立刻出现在电脑上。局域网加密直连，不经过任何服务器。">
<meta name="theme-color" content="#070A12">
<style>
:root {
  --bg: #070A12;
  --bg-2: #0B0F1A;
  --panel: rgba(255, 255, 255, .035);
  --panel-strong: rgba(255, 255, 255, .06);
  --line: rgba(255, 255, 255, .09);
  --line-strong: rgba(255, 255, 255, .16);
  --fg: #E9EBF2;
  --muted: #98A1B2;
  --brand: #7C74FF;
  --brand-2: #22D3EE;
  --brand-soft: rgba(124, 116, 255, .14);
  --radius: 16px;
}
* { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; scroll-behavior: smooth; }
body {
  margin: 0;
  background: var(--bg);
  color: var(--fg);
  font: 16px/1.8 "PingFang SC", "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", system-ui, sans-serif;
  word-break: break-word;
  overflow-x: hidden;
}
/* 技术感背景：网格 + 光晕 */
.bg {
  position: fixed;
  inset: 0;
  z-index: -1;
  background:
    radial-gradient(900px 520px at 12% -8%, rgba(124, 116, 255, .28), transparent 62%),
    radial-gradient(760px 460px at 92% 4%, rgba(34, 211, 238, .18), transparent 60%),
    radial-gradient(1100px 700px at 50% 108%, rgba(124, 116, 255, .16), transparent 60%),
    linear-gradient(180deg, #070A12 0%, #080C16 45%, #06080F 100%);
}
.bg::after {
  content: "";
  position: absolute;
  inset: 0;
  background-image:
    linear-gradient(rgba(255, 255, 255, .05) 1px, transparent 1px),
    linear-gradient(90deg, rgba(255, 255, 255, .05) 1px, transparent 1px);
  background-size: 46px 46px;
  -webkit-mask-image: radial-gradient(1100px 720px at 50% 0%, #000 20%, transparent 78%);
  mask-image: radial-gradient(1100px 720px at 50% 0%, #000 20%, transparent 78%);
}

/* 顶栏 */
.nav {
  position: sticky;
  top: 0;
  z-index: 10;
  backdrop-filter: saturate(160%) blur(14px);
  -webkit-backdrop-filter: saturate(160%) blur(14px);
  background: rgba(7, 10, 18, .72);
  border-bottom: 1px solid var(--line);
}
.nav-inner {
  max-width: 1000px;
  margin: 0 auto;
  padding: 12px 22px;
  display: flex;
  align-items: center;
  gap: 18px;
}
.nav .brand {
  font-weight: 700;
  letter-spacing: .5px;
  background: linear-gradient(92deg, var(--brand), var(--brand-2));
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}
.nav nav { margin-left: auto; display: flex; gap: 18px; flex-wrap: wrap; }
.nav nav a { color: var(--muted); font-size: 14px; border: 0; }
.nav nav a:hover { color: var(--fg); }

main { max-width: 920px; margin: 0 auto; padding: 0 22px 96px; }

/* 首屏 */
.hero { padding: 74px 0 10px; text-align: center; }
.hero h1 {
  font-size: 56px;
  line-height: 1.15;
  margin: 0 0 18px;
  letter-spacing: 2px;
  background: linear-gradient(96deg, #fff 8%, var(--brand) 48%, var(--brand-2) 92%);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}
.hero .tagline { font-size: 19px; color: var(--fg); margin: 0 auto 14px; max-width: 620px; }
.hero .meta { color: var(--muted); font-size: 14px; margin: 0 auto 28px; max-width: 680px; }
.cta { display: flex; gap: 14px; justify-content: center; flex-wrap: wrap; margin-bottom: 14px; }
.hero .contact { color: var(--muted); font-size: 13px; }
.hint-line { color: var(--muted); font-size: 13.5px; margin: 6px 0 0; }
.copy {
  font: inherit;
  font-size: 12.5px;
  color: var(--brand);
  background: var(--brand-soft);
  border: 1px solid rgba(124, 116, 255, .3);
  border-radius: 999px;
  padding: 2px 12px;
  margin-left: 8px;
  cursor: pointer;
  transition: .18s ease;
}
.copy:hover { background: rgba(124, 116, 255, .24); color: #fff; }
.hero-shot { margin: 46px 0 0; }
.hero-shot img {
  box-shadow: 0 20px 60px rgba(0, 0, 0, .55), 0 0 0 1px var(--line-strong);
}

/* 按钮 */
.btn {
  display: inline-block;
  padding: 13px 26px;
  border-radius: 999px;
  border: 1px solid var(--line-strong);
  color: var(--fg);
  font-size: 15px;
  background: var(--panel);
  transition: transform .18s ease, box-shadow .18s ease, border-color .18s ease;
  border-bottom: 1px solid var(--line-strong);
}
.btn:hover { transform: translateY(-2px); border-color: var(--brand); color: #fff; }
.btn.primary {
  border: 0;
  color: #fff;
  background: linear-gradient(96deg, var(--brand), #5B54EA 55%, var(--brand-2));
  box-shadow: 0 10px 30px rgba(124, 116, 255, .38);
}
.btn.ghost { color: var(--muted); background: transparent; }

/* 正文 */
.doc h2 {
  font-size: 25px;
  margin: 66px 0 18px;
  padding-left: 16px;
  position: relative;
}
.doc h2::before {
  content: "";
  position: absolute;
  left: 0;
  top: .34em;
  width: 5px;
  height: 1.05em;
  border-radius: 3px;
  background: linear-gradient(180deg, var(--brand), var(--brand-2));
}
.doc h3 { font-size: 18px; margin: 36px 0 12px; color: var(--brand); }
.doc p { margin: 14px 0; }
.doc strong { font-weight: 600; color: #fff; }
.doc a { color: var(--brand); text-decoration: none; border-bottom: 1px solid rgba(124, 116, 255, .35); }
.doc a:hover { border-bottom-color: var(--brand); }
.doc hr { border: 0; border-top: 1px solid var(--line); margin: 46px 0; }

.doc img {
  display: block;
  max-width: 100%;
  max-height: 600px;
  width: auto;
  height: auto;
  border-radius: var(--radius);
  border: 1px solid var(--line-strong);
  box-shadow: 0 14px 40px rgba(0, 0, 0, .5);
}
.doc figure, .hero-shot { display: flex; justify-content: center; margin: 26px 0; }

.doc code {
  font-family: Consolas, "SFMono-Regular", Menlo, monospace;
  font-size: .92em;
  background: rgba(124, 116, 255, .14);
  border: 1px solid rgba(124, 116, 255, .22);
  border-radius: 7px;
  padding: 1px 7px;
  color: #CFCBFF;
}
.doc pre {
  background: rgba(0, 0, 0, .45);
  border: 1px solid var(--line);
  border-radius: 14px;
  padding: 18px 20px;
  overflow-x: auto;
}
.doc pre code { background: none; border: 0; padding: 0; color: #B9C0D4; font-size: 13.5px; line-height: 1.75; }

.doc ul, .doc ol { padding-left: 26px; }
.doc li { margin: 7px 0; }
.doc ul.sublist { margin: 6px 0 10px; padding-left: 26px; }
.doc ul.sublist li { color: var(--muted); }

.doc blockquote {
  margin: 20px 0;
  padding: 15px 20px;
  background: var(--panel);
  border: 1px solid var(--line);
  border-left: 3px solid var(--brand);
  border-radius: 0 12px 12px 0;
  color: var(--muted);
}
.doc blockquote p { margin: 0; color: inherit; }

.table-wrap { overflow-x: auto; margin: 22px 0; border-radius: var(--radius); border: 1px solid var(--line); }
.doc table { border-collapse: collapse; width: 100%; font-size: 15px; background: var(--panel); }
.doc th, .doc td { border-bottom: 1px solid var(--line); padding: 12px 16px; text-align: left; vertical-align: top; }
.doc th { background: var(--brand-soft); font-weight: 600; white-space: nowrap; color: #fff; }
.doc tbody tr:last-child td { border-bottom: 0; }
.doc tbody tr:hover td { background: rgba(255, 255, 255, .03); }

footer {
  border-top: 1px solid var(--line);
  color: var(--muted);
  font-size: 13px;
  text-align: center;
  padding: 28px 22px 40px;
}
footer a { color: var(--muted); }

/* 入场动效（无 JS 时也能看，因为默认就是可见的） */
.reveal { opacity: 0; transform: translateY(18px); transition: opacity .6s ease, transform .6s ease; }
.reveal.on { opacity: 1; transform: none; }

@media (max-width: 640px) {
  .nav nav { gap: 12px; }
  main { padding: 0 16px 70px; }
  .hero { padding: 44px 0 6px; }
  .hero h1 { font-size: 38px; }
  .hero .tagline { font-size: 16px; }
  .doc h2 { font-size: 21px; margin-top: 48px; }
  .doc img { max-height: 440px; }
  .btn { padding: 12px 20px; font-size: 14px; }
}
</style>
</head>
<body>
<div class="bg" aria-hidden="true"></div>

<header class="nav">
  <div class="nav-inner">
    <span class="brand">${escapeHtml(heroTitle)}</span>
    <nav>
      <a href="#features">特性</a>
      <a href="#download">下载</a>
      <a href="#guide">教程</a>
      <a href="#security">安全</a>
      <a href="${repo}" target="_blank" rel="noopener">GitHub</a>
    </nav>
  </div>
</header>

<main>
  <section class="hero">
    <h1>${escapeHtml(heroTitle)}</h1>
    <p class="tagline">${inline(heroTagline)}</p>
    <p class="meta">${inline(heroMeta)}</p>
    <div class="cta">
        ${cta}
    </div>
    ${lanzouNote}
    <p class="contact">${heroContact}</p>
    ${heroShot}
  </section>

  <article class="doc">
${article}
  </article>
</main>

<footer>
  ${escapeHtml(heroTitle)} · <a href="${repo}" target="_blank" rel="noopener">GitHub 仓库</a> ·
  <a href="${releases}" target="_blank" rel="noopener">下载</a>
</footer>

<script>
// 复制蓝奏云密码
for (const button of document.querySelectorAll('[data-copy]')) {
  button.addEventListener('click', async () => {
    const text = button.getAttribute('data-copy') || '';
    try {
      await navigator.clipboard.writeText(text);
      button.textContent = '已复制';
    } catch (_) {
      button.textContent = text;
    }
    setTimeout(() => { button.textContent = '复制'; }, 1600);
  });
}

// 滚动淡入：默认内容可见，JS 可用时才加动效
if ('IntersectionObserver' in window) {
  const targets = document.querySelectorAll('.doc h2, .doc figure, .doc table, .doc blockquote');
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (entry.isIntersecting) {
        entry.target.classList.add('on');
        observer.unobserve(entry.target);
      }
    }
  }, { rootMargin: '0px 0px -8% 0px', threshold: .05 });
  for (const target of targets) {
    target.classList.add('reveal');
    observer.observe(target);
  }
}
</script>
</body>
</html>
`;

fs.writeFileSync(path.join(docsDir, 'index.html'), page, 'utf8');
console.log(`已生成 docs/index.html（${(Buffer.byteLength(page) / 1024).toFixed(1)} KB）`);

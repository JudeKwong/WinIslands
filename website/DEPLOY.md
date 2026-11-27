# WinIslands 官网 — 部署说明（Cloudflare Pages）

## 网站内容

| 文件 | 说明 |
|---|---|
| `index.html` | 官网首页（单页滚动） |
| `css/style.css` | 样式（深色玻璃拟态、响应式） |
| `js/main.js` | 交互（导航、滚动渐入、灵动岛演示动画） |
| `assets/favicon.svg` | 站点图标 |
| `assets/screenshot-*.png` | 产品真实截图 |

> 纯静态站点，无任何构建步骤、无后端、无第三方统计。下载链接直连 GitHub Releases。

## 部署到 Cloudflare Pages

1. 打开 [Cloudflare Dashboard](https://dash.cloudflare.com) → **Workers & Pages** → **Create** → **Pages** → **Upload assets**（直接上传，无需 Git）。
2. 把 `website/` 目录**内**的所有文件和文件夹（`index.html`、`css/`、`js/`、`assets/`）一起拖入上传；项目名建议填 `winisland`（沿用现有 Cloudflare Pages 项目）。
3. 部署完成后会得到一个 `*.pages.dev` 地址，先在浏览器确认内容正常。

## 绑定域名 WinIslands.JudeKwong.com

1. 进入该项目 → **Settings** → **Custom domains** → **Add custom domain**。
2. 输入 `WinIslands.JudeKwong.com` → 继续。
3. Cloudflare 会自动在 DNS 中创建一条 **CNAME** 记录：`WinIslands` → `<项目名>.pages.dev`（代理状态默认开启）。
4. 等待几秒至数分钟生效，访问 `https://WinIslands.JudeKwong.com` 即可。

> 由于 JudeKwong.com 的 DNS 已在 Cloudflare，添加子域名不会中断主域名现有服务，也不需要额外设置。
> 若 Custom domains 提示需在 DNS 手动添加，用楼上同一条 CNAME 记录即可（名称 `WinIslands`，目标 `<项目名>.pages.dev`，代理开启）。

## 更新网站

- 改完本地文件后，回到该 Pages 项目再次 **Upload assets** 上传即可，Custom domain 会自动指向最新 production 部署。
- 每次上传需删除旧的 deployment 或直接覆盖（Cloudflare 会生成新版本，可随时回滚）。

## 本地预览

直接双击打开 `website/index.html` 即可（无需服务器），或运行：

```powershell
Start-Process "E:\MyFiles\Program\WinIsland\website\index.html"
```
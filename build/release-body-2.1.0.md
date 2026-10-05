## WinIslands 2.1.0（正式版 / Stable）

### 更新内容

- **🌱 内容视差位移（更接近 iOS）**：展开时文字不再只是原地淡入——内容从稍小、稍上方的位置随卡片生长浮现（轻微缩放 + 上移），收起时轻微收缩上移淡出，与卡片形变同频，摆脱静态淡入的生硬感
- **🔄 胶囊行即时接棒（消除空洞期）**：收起时胶囊行在内容淡出一半时就开始浮现，与内容淡出重叠——展开中途点收起的打断场景不再出现「内容消失后胶囊迟迟不出现」的闪烁
- **🎞️ 所有动画风格统一视差**：Spring 弹簧路径与 Soft/Elastic/Fade 故事板路径都应用相同的缩放进场/退场，观感一致
- **🧪 视差曲线纯函数化 + 回归测试**：ContentParallax 抽为可单测的纯函数，新增 3 项测试，共 261 项全部通过

---

## WinIslands 2.1.0 (Stable)

### What's New

- **🌱 Content parallax (closer to iOS)**: Text no longer fades in place — expanded content now visibly grows into place from a slightly smaller, higher position (gentle scale + rise), and gently shrinks/drifts upward while collapsing, in sync with the card morph
- **🔄 Pill row takes over instantly (no dead air)**: On collapse the pill row starts reappearing once the content is half faded, overlapping the fade-out — interrupting expansion midway no longer leaves a flickering gap between content and pill
- **🎞️ Unified parallax across all animation styles**: Both the Spring physics path and the Soft/Elastic/Fade storyboard paths use the same scale/translate entrance and exit
- **🧪 Pure parallax curves + regression testing**: `ContentParallax` extracted as testable pure functions with 3 new tests — all 261 tests pass

---

> 📝 / Note: 更多细节见 README · See README for details.
## WinIslands 2.0.9（正式版 / Stable）

### 更新内容

- **🕊️ 内容淡入跟随形变（消除“文字从上面出现”）**：展开时内容不再在卡片只长到七成时就已全不透明地悬在上方——内容淡入改为略滞后于卡片形变（形状先导、内容随后浮现），配合「收起时内容先淡出再收拢」，全程没有“文字从顶部冒出来”的割裂感
- **🔄 胶囊行交叉淡入解耦**：胶囊行（紧凑内容）与展开内容各自的淡入淡出节奏独立——展开时胶囊行在前 25% 让位、收起时胶囊行在收拢的最后阶段浮现，两手交接丝滑不生硬、不闪烁
- **🧪 交叉淡入曲线纯函数化**：淡入响应系数与胶囊行透明度曲线抽为可单测的纯函数（CrossFadeCurves），新增 6 项单元测试
- **✅ 回归测试**：258 项单元测试全部通过

---

## WinIslands 2.0.9 (Stable)

### What's New

- **🕊️ Content fade follows the shape morph (fixes “text popping from above”)**: On expand, content no longer sits fully opaque at the top while the card is only ~70% grown — the content fade now trails the shape (shape leads, content follows), and on collapse content fades out before the card shrinks, so no text is ever “born” out of the top edge
- **🔄 Decoupled pill-row cross-fade**: The compact pill row and expanded content now have independent fade rhythms — the pill yields during the first quarter of expansion and reappears only in the final phase of collapse, giving a smooth, flicker-free hand-off
- **🧪 Pure cross-fade curves**: The fade timing factors and pill-opacity curve are extracted into testable pure functions (`CrossFadeCurves`) with 6 new unit tests
- **✅ Regression testing**: All 258 unit tests passed

---

> 📝 / Note: 更多细节见 README · See README for details.
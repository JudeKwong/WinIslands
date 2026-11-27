from pathlib import Path
p = Path(r'E:\MyFiles\Program\WinIsland\website\index.html')
s = p.read_text(encoding='utf-8')
s = s.replace('    <!-- ===== 官网宣传视觉 ===== -->\n', '')
s = s.replace('WinIslands 3.1.6beta3 实际运行截图：展开态灵动岛。', 'WinIslands 高级主视觉：深色玻璃质感灵动岛。')
s = s.replace('WinIslands 3.1.6beta3 实际运行截图：紧凑态组件胶囊。', 'WinIslands 组件视觉：多枚玻璃胶囊的组合。')
p.write_text(s, encoding='utf-8')
print('patched')

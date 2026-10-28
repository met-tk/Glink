import os
import math

def generate_variants():
    # 变体 1: 灵光共鸣 (G-Spark Balance)
    # 中心四芒星位于 (246, 256)，G 的轨迹半径 134，右侧横梁停在 x=316，留出 40px 的黄金负空间
    svg_v1 = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="100%" height="100%">
  <rect width="512" height="512" fill="none"/>
  <!-- 容器背景: 暮光暖炭灰 #151413，带平滑连续大圆角 112px -->
  <rect x="32" y="32" width="448" height="448" rx="112" fill="#151413"/>
  
  <!-- 纯色主体: 暮光强调色 #E8956B -->
  <g fill="#E8956B">
    <!-- 字母 G 主体轨迹 (圆角端头，粗壮有力) -->
    <path d="M 358 174 A 132 132 0 1 0 388 256 L 316 256" 
          fill="none" 
          stroke="#E8956B" 
          stroke-width="44" 
          stroke-linecap="round" 
          stroke-linejoin="round"/>
    
    <!-- 核心灵光 (Spark): 四芒微圆角曲线，象征四类灵感资产(文/音/图/视)随光沉淀 -->
    <!-- 中心位于 (244, 256)，大小 88x88，尖端圆润，内凹弧线平滑舒展 -->
    <path d="M 244 212 
             C 244 238 230 256 200 256 
             C 230 256 244 274 244 300 
             C 244 274 258 256 288 256 
             C 258 256 244 238 244 212 Z"/>
  </g>
</svg>"""

    # 变体 2: 纯色反色版 (暖杏橙底板 + 炭灰镂空/单色剪影)
    svg_v2 = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="100%" height="100%">
  <rect width="512" height="512" fill="none"/>
  <!-- 容器背景: 暮光暖杏橙 #E8956B，带平滑连续大圆角 112px -->
  <rect x="32" y="32" width="448" height="448" rx="112" fill="#E8956B"/>
  
  <!-- 纯色主体: 深炭灰 #151413 -->
  <g fill="#151413">
    <path d="M 358 174 A 132 132 0 1 0 388 256 L 316 256" 
          fill="none" 
          stroke="#151413" 
          stroke-width="44" 
          stroke-linecap="round" 
          stroke-linejoin="round"/>
    
    <path d="M 244 212 
             C 244 238 230 256 200 256 
             C 230 256 244 274 244 300 
             C 244 274 258 256 288 256 
             C 258 256 244 238 244 212 Z"/>
  </g>
</svg>"""

    # 变体 3: 双重连结与空间卡片 (Link & Canvas Cards)
    # 两张交叠流动的圆角卡片通过纯色构筑字母 G，卡片右侧折角形成闭环连结，极具空间感
    svg_v3 = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="100%" height="100%">
  <rect width="512" height="512" fill="none"/>
  <rect x="32" y="32" width="448" height="448" rx="112" fill="#151413"/>
  
  <g fill="none" stroke="#E8956B" stroke-width="40" stroke-linecap="round" stroke-linejoin="round">
    <!-- 外层主卡片轮廓，右上开口 -->
    <path d="M 350 160 C 310 120 220 120 170 170 C 120 220 120 300 170 350 C 220 400 310 400 350 350 C 375 320 380 290 380 256 L 270 256" />
    
    <!-- 内部灵光焦点 (菱形圆角微光) -->
    <path d="M 246 226 L 276 256 L 246 286 L 216 256 Z" 
          fill="#E8956B" stroke="none"/>
  </g>
</svg>"""

    # 变体 4: 极简纯粹单闭合几何体 (Monolithic Solid G)
    # 纯色一体成型，全圆角曲面，将 G 的环抱与星芒一体贯通
    svg_v4 = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="100%" height="100%">
  <rect width="512" height="512" fill="none"/>
  <rect x="32" y="32" width="448" height="448" rx="112" fill="#151413"/>
  <g fill="#E8956B">
    <!-- 纯色连续圆角路径构建的现代几何 G -->
    <!-- 外圈圆角，横梁与内部星芒构成完美的正负空间平衡 -->
    <path d="M 358 174 A 132 132 0 1 0 388 256 L 316 256" 
          fill="none" 
          stroke="#E8956B" 
          stroke-width="48" 
          stroke-linecap="round" 
          stroke-linejoin="round"/>
    
    <!-- 精致的八方微光核心: 4个主芒 + 4个次芒微倒角，沉淀感更加强烈 -->
    <path d="M 244 204 
             C 244 234 228 256 192 256 
             C 228 256 244 278 244 308 
             C 244 278 260 256 296 256 
             C 260 256 244 234 244 204 Z"/>
  </g>
</svg>"""

    # 变体 5: 无背景纯图形 (Transparent / Monochrome Symbol)
    # 可直接使用 currentColor 适配任何前景色或作为控件 Icon
    svg_v5 = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="100%" height="100%">
  <!-- Glink 纯色矢量符号 (无底板，支持 currentColor 继承或单色设定) -->
  <g fill="currentColor">
    <path d="M 358 174 A 132 132 0 1 0 388 256 L 316 256" 
          fill="none" 
          stroke="currentColor" 
          stroke-width="46" 
          stroke-linecap="round" 
          stroke-linejoin="round"/>
    
    <path d="M 244 206 
             C 244 236 228 256 196 256 
             C 228 256 244 276 244 306 
             C 244 276 260 256 292 256 
             C 260 256 244 236 244 206 Z"/>
  </g>
</svg>"""

    out_dir = r"x:\AI project\Glink\design"
    os.makedirs(out_dir, exist_ok=True)
    
    with open(os.path.join(out_dir, "glink-icon-dark.svg"), "w", encoding="utf-8") as f:
        f.write(svg_v1)
    with open(os.path.join(out_dir, "glink-icon-accent.svg"), "w", encoding="utf-8") as f:
        f.write(svg_v2)
    with open(os.path.join(out_dir, "glink-icon-solid.svg"), "w", encoding="utf-8") as f:
        f.write(svg_v4)
    with open(os.path.join(out_dir, "glink-symbol-monochrome.svg"), "w", encoding="utf-8") as f:
        f.write(svg_v5)
        
    print("SVGs successfully written.")

generate_variants()
